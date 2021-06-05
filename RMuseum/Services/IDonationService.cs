﻿using RMuseum.Models.Accounting;
using RMuseum.Models.Accounting.ViewModels;
using RSecurityBackend.Models.Generic;
using System;
using System.Threading.Tasks;

namespace RMuseum.Services
{
    /// <summary>
    /// donation service
    /// </summary>
    public interface IDonationService
    {
        /// <summary>
        /// new donation
        /// </summary>
        /// <param name="editingUserId"></param>
        /// <param name="donation"></param>
        /// <returns></returns>
        Task<RServiceResult<GanjoorDonationViewModel>> AddDonation(Guid editingUserId, GanjoorDonationViewModel donation);

        /// <summary>
        /// delete donation
        /// </summary>
        /// <param name="editingUserId"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        Task<RServiceResult<bool>> DeleteDonation(Guid editingUserId, int id);

        /// <summary>
        /// new expense
        /// </summary>
        /// <param name="editingUserId"></param>
        /// <param name="expense"></param>
        /// <returns></returns>
        Task<RServiceResult<GanjoorExpense>> AddExpense(Guid editingUserId, GanjoorExpense expense);

        /// <summary>
        /// parse html of https://ganjoor.net/donate/ and fill the records
        /// </summary>
        /// <returns></returns>
        Task<RServiceResult<bool>> InitializeRecords();

        /// <summary>
        /// regenerate donations page
        /// </summary>
        /// <param name="editingUserId"></param>
        /// <returns></returns>
        Task<RServiceResult<bool>> RegenerateDonationsPage(Guid editingUserId);
    }
}
