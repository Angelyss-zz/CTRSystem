﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CTRSystem.Configuration;
using TShockAPI;
using TShockAPI.DB;

namespace CTRSystem.DB
{
	public class Contributor
	{
		private TSPlayer _receiver;

		/// <summary>
		/// The key used when storing a contributor object as data.
		/// </summary>
		public const string DataKey = "CTRS_Con";

		/// <summary>
		/// Contributor Id used internally for storage and loading.
		/// </summary>
		public int Id { get; set; }

		/// <summary>
		/// List of user account IDs authenticated with this contributor object.
		/// </summary>
		public List<int> Accounts { get; set; }

		public int? XenforoId { get; set; }

		public float TotalCredits { get; set; }

		/// <summary>
		/// The date of the last donation made by the contributor.
		/// Always convert to string in the sortable ("s") DateTime format.
		/// </summary>
		public DateTime LastDonation { get; set; }

		/// <summary>
		/// The amount of credits generated by the contributor's last donation.
		/// </summary>
		public float LastAmount { get; set; }

		public int Tier { get; set; }

		public Color? ChatColor { get; set; }

		public Notifications Notifications { get; set; }

		public Settings Settings { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="Contributor"/> class with the given
		/// <paramref name="contributorId"/>.
		/// </summary>
		/// <param name="contributorId">The contributor ID in the database.</param>
		public Contributor(int contributorId)
		{
			Accounts = new List<int>();
			Id = contributorId;
			Tier = 1;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Contributor"/> class from the given
		/// <paramref name="user"/> account.
		/// </summary>
		/// <param name="user">The user account to be registered to this contributor.</param>
		public Contributor(User user) : this(0)
		{
			Accounts.Add(user.ID);
		}

		/// <summary>
		///  Stop listening to events. Used when disposing of this contributor object.
		/// </summary>
		public void Unlisten()
		{
			CTRS.Contributors.ContributorUpdate -= OnUpdate;
			CTRS.Contributors.Transaction -= OnTransaction;
		}

		/// <summary>
		/// Start listening to contributor related events fired by various contribution modules.
		/// </summary>
		/// <param name="player">If set, will send notification messages to a player whenever possible.</param>
		public void Listen(TSPlayer player = null)
		{
			_receiver = player;

			CTRS.Contributors.ContributorUpdate += OnUpdate;
			CTRS.Contributors.Transaction += OnTransaction;
		}

		public async void OnTransaction(object sender, TransactionEventArgs e)
		{
			if (e.ContributorId == Id)
			{
				LastAmount = e.Credits;
				if (e.Date.HasValue)
					LastDonation = e.Date.Value;
				TotalCredits += e.Credits;

				// If the player is online, notify them about the transaction
				foreach (string s in Texts.SplitIntoLines(
					CTRS.Config.Texts.FormatNewDonation(_receiver, this, LastAmount)))
				{
					_receiver.SendInfoMessage(s);
				}

				// Check for tier upgrades
				int oldTier = Tier;
				await CTRS.Tiers.UpgradeTier(this, true);
				if (Tier != oldTier)
				{
					// Delay the message according to the config
					await Task.Delay(CTRS.Config.NotificationCheckSeconds);

					foreach (string s in Texts.SplitIntoLines(
						CTRS.Config.Texts.FormatNewTier(_receiver, this, CTRS.Tiers.Get(Tier))))
					{
						_receiver.SendInfoMessage(s);
					}
				}

				// Suppress notifications from being set on the database
				e.SuppressNotifications = true;
			}
		}

		public void OnUpdate(object sender, ContributorUpdateEventArgs e)
		{
			if (e.ContributorId == Id)
			{
				if ((e.Updates & ContributorUpdates.XenforoID) == ContributorUpdates.XenforoID)
					XenforoId = e.XenforoId;
				if ((e.Updates & ContributorUpdates.TotalCredits) == ContributorUpdates.TotalCredits)
					TotalCredits = e.TotalCredits;
				if ((e.Updates & ContributorUpdates.LastDonation) == ContributorUpdates.LastDonation)
					LastDonation = e.LastDonation;
				if ((e.Updates & ContributorUpdates.LastAmount) == ContributorUpdates.LastAmount)
					LastAmount = e.LastAmount;
				if ((e.Updates & ContributorUpdates.Tier) == ContributorUpdates.Tier)
					Tier = e.Tier;
				if ((e.Updates & ContributorUpdates.ChatColor) == ContributorUpdates.ChatColor)
					ChatColor = e.ChatColor;
				if ((e.Updates & ContributorUpdates.Notifications) == ContributorUpdates.Notifications)
					Notifications = e.Notifications;
				if ((e.Updates & ContributorUpdates.Settings) == ContributorUpdates.Settings)
					Settings = e.Settings;
			}
		}
	}
}
