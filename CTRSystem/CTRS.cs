﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CTRSystem.DB;
using CTRSystem.Extensions;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using Config = CTRSystem.Configuration.ConfigFile;
using Texts = CTRSystem.Configuration.Texts;

namespace CTRSystem
{
	[ApiVersion(1, 22)]
	public class CTRS : TerrariaPlugin
	{
		private static DateTime lastRefresh;
		private static DateTime lastNotification;

		public override string Author
		{
			get { return "Enerdy"; }
		}

		public override string Description
		{
			get { return "Keeps track of server contributors and manages their privileges."; }
		}

		public override string Name
		{
			get { return "Contributions Track & Reward System"; }
		}

		public override Version Version
		{
			get { return Assembly.GetExecutingAssembly().GetName().Version; }
		}

		public CTRS(Main game) : base(game)
		{
			lastRefresh = DateTime.Now;
			lastNotification = DateTime.Now;
		}

		public static Config Config { get; private set; }

		public static IDbConnection Db { get; private set; }

		public static ContributorManager Contributors { get; private set; }

		public static TierManager Tiers { get; private set; }

		public static Version PublicVersion
		{
			get { return Assembly.GetExecutingAssembly().GetName().Version; }
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				//PlayerHooks.PlayerChat -= OnChat;
				GeneralHooks.ReloadEvent -= OnReload;
				PlayerHooks.PlayerPermission -= OnPlayerPermission;
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
				ServerApi.Hooks.GameUpdate.Deregister(this, UpdateTiers);
				ServerApi.Hooks.GameUpdate.Deregister(this, UpdateNotifications);
				ServerApi.Hooks.ServerChat.Deregister(this, OnChat);
			}
		}

		public override void Initialize()
		{
			//PlayerHooks.PlayerChat += OnChat;
			GeneralHooks.ReloadEvent += OnReload;
			PlayerHooks.PlayerPermission += OnPlayerPermission;
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
			ServerApi.Hooks.GameUpdate.Register(this, UpdateTiers);
			ServerApi.Hooks.GameUpdate.Register(this, UpdateNotifications);
			ServerApi.Hooks.ServerChat.Register(this, OnChat);
		}

		async void OnChat(ServerChatEventArgs e)
		{
			if (e.Handled)
				return;

			// A quick check to reduce DB work when this feature isn't in use
			if (String.IsNullOrWhiteSpace(Config.ContributorChatFormat) || Config.ContributorChatFormat == TShock.Config.ChatFormat)
				return;

			var player = TShock.Players[e.Who];
			if (player == null)
				return;

			if (e.Text.Length > 500)
				return;

			// If true, the message is a command, so we skip it
			if ((e.Text.StartsWith(TShockAPI.Commands.Specifier) || e.Text.StartsWith(TShockAPI.Commands.SilentSpecifier))
				&& !String.IsNullOrWhiteSpace(e.Text.Substring(1)))
				return;

			// Player needs to be able to talk, not be muted, and must be logged in
			if (!player.HasPermission(Permissions.canchat) || player.mute || !player.IsLoggedIn)
				return;

			// At this point, ChatAboveHeads is not supported, but it could be a thing in the future
			if (!TShock.Config.EnableChatAboveHeads)
			{
				Contributor con;
				try
				{
					con = await Contributors.GetAsync(player.User.ID);
				}
				catch (ContributorManager.ContributorNotFoundException)
				{
					return;
				}

				Tier tier;
				try
				{
					tier = await Tiers.GetByCreditsAsync(con.TotalCredits);
				}
				catch (TierManager.TierNotFoundException)
				{
					return;
				}

				/* Contributor chat format:
					{0} - group name
					{1} - group prefix
					{2} - player name
					{3} - group suffix
					{4} - message text
					{5} - tier shortname
					{6} - tier name
					{7} - webID

				 */
				var text = String.Format(Config.ContributorChatFormat, player.Group.Name, player.Group.Prefix, player.Name,
					player.Group.Suffix, e.Text, tier.ShortName ?? "", tier.Name ?? "", con.WebID ?? -1);
				PlayerHooks.OnPlayerChat(player, e.Text, ref text);
				Color? color = con.ChatColor;
				if (!color.HasValue)
					color = new Color(player.Group.R, player.Group.G, player.Group.B);
				TShock.Utils.Broadcast(text, color.Value.R, color.Value.G, color.Value.B);
				e.Handled = true;
			}
		}

		void OnInitialize(EventArgs e)
		{
			#region Config

			string path = Path.Combine(TShock.SavePath, "CTRSystem", "CTRS-Config.json");
			Config = Config.Read(path);

			#endregion

			#region Commands

			TShockAPI.Commands.ChatCommands.Add(new Command(Permissions.Commands, Commands.Contributions,
				(new List<string>(Config.AdditionalCommandAliases) { "ctrs" }).ToArray())
			{
				HelpText = "Manages contributor settings. You must have contributed at least once before using this command."
			});

			#endregion

			#region DB

			if (Config.StorageType.Equals("mysql", StringComparison.OrdinalIgnoreCase))
			{
				string[] host = Config.MySqlHost.Split(':');
				Db = new MySqlConnection()
				{
					ConnectionString = String.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
					host[0],
					host.Length == 1 ? "3306" : host[1],
					Config.MySqlDbName,
					Config.MySqlUsername,
					Config.MySqlPassword)
				};
			}
			else if (Config.StorageType.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
				Db = new SqliteConnection(String.Format("uri=file://{0},Version=3",
					Path.Combine(TShock.SavePath, "CTRSystem", "CTRS-Data.sqlite")));
			else
				throw new InvalidOperationException("Invalid storage type!");

			#endregion

			Contributors = new ContributorManager(Db);
			Tiers = new TierManager(Db);
		}

		async void OnPlayerPermission(PlayerPermissionEventArgs e)
		{
			// If the player isn't logged it, he's certainly not a contributor
			if (!e.Player.IsLoggedIn || e.Player.User == null)
				return;

			// Mirror TShock checks to reduce DB load
			if (e.Player.tempGroup != null)
			{
				if (e.Player.tempGroup.HasPermission(e.Permission))
				{
					e.Handled = true;
					return;
				}
			}
			else
			{
				if (e.Player.Group.HasPermission(e.Permission))
				{
					e.Handled = true;
					return;
				}
			}

			Contributor con;
			try
			{
				con = await Contributors.GetAsync(e.Player.User.ID);
			}
			catch (ContributorManager.ContributorNotFoundException)
			{
				return;
			}

			// Check if a Tier Update is pending, and if it is, perform it before anything else
			// NOTE: If this occurs, there is a good chance that a delay will be noticeable
			if ((con.Notifications & Notifications.TierUpdate) == Notifications.TierUpdate)
			{
				ContributorUpdates updates = 0;
				Tier newTier = await Tiers.GetByCreditsAsync(con.TotalCredits);
				if (newTier.ID != con.Tier)
				{
					con.Tier = newTier.ID;
					updates |= ContributorUpdates.Tier;
				}
				con.Notifications ^= Notifications.TierUpdate;
				updates |= ContributorUpdates.Notifications;

				await Contributors.UpdateAsync(con, updates);
			}

			Tier tier = await Tiers.GetAsync(con.Tier);
			e.Handled = tier.Permissions.HasPermission(e.Permission);
		}

		void OnReload(ReloadEventArgs e)
		{
			string path = Path.Combine(TShock.SavePath, "CTRSystem", "CTRS-Config.json");
			Config = Config.Read(path);
		}

		async void UpdateNotifications(EventArgs e)
		{
			if ((DateTime.Now - lastNotification).TotalSeconds >= Config.NotificationCheckSeconds)
			{
				lastNotification = DateTime.Now;
				foreach (TSPlayer player in TShock.Players.Where(p => p != null && p.Active && p.IsLoggedIn && p.User != null))
				{
					Contributor con;
					try
					{
						con = await Contributors.GetAsync(player.User.ID);
					}
					catch (ContributorManager.ContributorNotFoundException)
					{
						continue;
					}

					ContributorUpdates updates = 0;
					if ((con.Notifications & Notifications.TierUpdate) == Notifications.TierUpdate)
					{
						Tier tier = await Tiers.GetByCreditsAsync(con.TotalCredits);
						if (con.Tier != tier.ID)
						{
							con.Tier = tier.ID;
							updates |= ContributorUpdates.Tier;
						}
						con.Notifications ^= Notifications.TierUpdate;
						updates |= ContributorUpdates.Notifications;
					}

					if ((con.Notifications & Notifications.Introduction) != Notifications.Introduction)
					{
						// Do Introduction message
						foreach (string s in Texts.SplitIntoLines(Config.Texts.FormatIntroduction(player)))
						{
							player.SendInfoMessage(s);
						}
						con.Notifications |= Notifications.Introduction;
						updates |= ContributorUpdates.Notifications;
					}
					else if ((con.Notifications & Notifications.NewDonation) == Notifications.NewDonation)
					{
						// Do NewDonation message
						foreach (string s in Texts.SplitIntoLines(Config.Texts.FormatNewDonation(player)))
						{
							player.SendInfoMessage(s);
						}
						con.Notifications ^= Notifications.NewDonation;
						updates |= ContributorUpdates.Notifications;
					}
					else if ((con.Notifications & Notifications.NewTier) == Notifications.NewTier)
					{
						// Do Tier Rank Up message
						foreach (string s in Texts.SplitIntoLines(Config.Texts.FormatNewTier(player)))
						{
							player.SendInfoMessage(s);
						}
						con.Notifications ^= Notifications.NewTier;
						updates |= ContributorUpdates.Notifications;
					}

					if (updates != 0 && !await Contributors.UpdateAsync(con, updates) && Config.LogDatabaseErrors)
						TShock.Log.ConsoleError("CTRS-DB: something went wrong while updating a contributor's notifications.");
				}
			}
		}

		void UpdateTiers(EventArgs e)
		{
			if ((DateTime.Now - lastRefresh).TotalMinutes >= Config.TierRefreshMinutes)
			{
				lastRefresh = DateTime.Now;
				Tiers.Refresh();
			}
		}
	}
}
