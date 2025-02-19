﻿using Microsoft.EntityFrameworkCore;
using RMuseum.Models.Ganjoor;
using RSecurityBackend.Models.Generic;
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RMuseum.Models.Auth.ViewModel;
using RMuseum.Models.GanjoorIntegration;
using RSecurityBackend.Models.Auth.ViewModels;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text;
using RMuseum.DbContext;
using RSecurityBackend.Services.Implementation;
using RSecurityBackend.Models.Generic.Db;
using RMuseum.Models.PDFLibrary;
using DNTPersianUtils.Core;

namespace RMuseum.Services.Implementation
{
    /// <summary>
    /// IGanjoorService implementation
    /// </summary>
    public partial class GanjoorService : IGanjoorService
    {
        /// <summary>
        /// synchronize naskban links
        /// </summary>
        /// <param name="ganjoorUserId"></param>
        /// <param name="naskbanUserName"></param>
        /// <param name="naskbanPassword"></param>
        public void SynchronizeNaskbanLinks(Guid ganjoorUserId, string naskbanUserName, string naskbanPassword)
        {
            _backgroundTaskQueue.QueueBackgroundWorkItem
                           (
                           async token =>
                           {
                               using (RMuseumDbContext context = new RMuseumDbContext(new DbContextOptions<RMuseumDbContext>())) //this is long running job, so _context might be already been freed/collected by GC
                               {
                                   LongRunningJobProgressServiceEF jobProgressServiceEF = new LongRunningJobProgressServiceEF(context);
                                   var job = (await jobProgressServiceEF.NewJob("SynchronizeNaskbanLinks", "Query data")).Result;
                                   var res = await _SynchronizeNaskbanLinksAsync(context, ganjoorUserId, naskbanUserName, naskbanPassword);
                                   if (!string.IsNullOrEmpty(res.ExceptionString))
                                   {
                                       await jobProgressServiceEF.UpdateJob(job.Id, 100, "", false, res.ExceptionString);
                                   }
                                   else
                                   {
                                       await jobProgressServiceEF.UpdateJob(job.Id, 100, "", true);
                                   }
                               }
                           });
        }

        /// <summary>
        /// synchronize naskban links
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ganjoorUserId"></param>
        /// <param name="naskbanUserName"></param>
        /// <param name="naskbanPassword"></param>
        /// <returns>number of synched items</returns>
        private async Task<RServiceResult<int>> _SynchronizeNaskbanLinksAsync(RMuseumDbContext context, Guid ganjoorUserId, string naskbanUserName, string naskbanPassword)
        {
            try
            {
                LoggedOnUserModelEx loggedOnUser;
                using (HttpClient client = new HttpClient())
                {
                    LoginViewModel loginViewModel = new LoginViewModel()
                    {
                        Username = naskbanUserName,
                        Password = naskbanPassword,
                        ClientAppName = "Ganjoor API",
                        Language = "fa-IR"
                    };
                    var loginResponse = await client.PostAsync("https://api.naskban.ir/api/users/login", new StringContent(JsonConvert.SerializeObject(loginViewModel), Encoding.UTF8, "application/json"));

                    if (loginResponse.StatusCode != HttpStatusCode.OK)
                    {
                        return new RServiceResult<int>(0, "login error: " + JsonConvert.DeserializeObject<string>(await loginResponse.Content.ReadAsStringAsync()));
                    }
                    loggedOnUser = JsonConvert.DeserializeObject<LoggedOnUserModelEx>(await loginResponse.Content.ReadAsStringAsync());
                }


                using (HttpClient secureClient = new HttpClient())
                {
                    secureClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loggedOnUser.Token);
                    var unsyncedResponse = await secureClient.GetAsync("https://api.naskban.ir/api/pdf/ganjoor/unsynched");
                    if (!unsyncedResponse.IsSuccessStatusCode)
                    {
                        return new RServiceResult<int>(0, "unsync error: " + JsonConvert.DeserializeObject<string>(await unsyncedResponse.Content.ReadAsStringAsync()));
                    }
                    var unsynchronizeds = JsonConvert.DeserializeObject<PDFGanjoorLink[]>(await unsyncedResponse.Content.ReadAsStringAsync());
                    foreach (var unsynchronized in unsynchronizeds)
                    {
                        bool isTextOriginalSource =
                            unsynchronized.IsTextOriginalSource
                            &&
                            await context.GanjoorLinks.Where(l => l.GanjoorPostId == unsynchronized.GanjoorPostId && l.IsTextOriginalSource).AnyAsync() == false
                            &&
                            await context.PinterestLinks.Where(l => l.GanjoorPostId == unsynchronized.GanjoorPostId && l.IsTextOriginalSource).AnyAsync() == false
                            ;
                        if (false == await context.PinterestLinks.Where(p => p.NaskbanLinkId == unsynchronized.Id).AnyAsync())
                        {
                            PinterestLink link = new PinterestLink()
                            {
                                GanjoorPostId = unsynchronized.GanjoorPostId,
                                GanjoorTitle = unsynchronized.GanjoorTitle,
                                GanjoorUrl = unsynchronized.GanjoorUrl,
                                AltText = unsynchronized.PDFPageTitle,
                                LinkType = LinkType.Naskban,
                                PinterestUrl = $"https://naskban.ir/{unsynchronized.PDFBookId}/{unsynchronized.PageNumber}",
                                PinterestImageUrl = unsynchronized.ExternalThumbnailImageUrl,
                                ReviewResult = ReviewResult.Approved,
                                SuggestionDate = DateTime.Now,
                                SuggestedById = ganjoorUserId,
                                Synchronized = true,
                                ReviewerId = ganjoorUserId,
                                IsTextOriginalSource = isTextOriginalSource,
                                PDFBookId = unsynchronized.PDFBookId,
                                PageNumber = unsynchronized.PageNumber,
                                NaskbanLinkId = unsynchronized.Id,
                                MatchPercent = 0,
                            };
                            context.PinterestLinks.Add(link);
                            await context.SaveChangesAsync();
                        }
                        await secureClient.PutAsync($"https://api.naskban.ir/api/pdf/ganjoor/sync/{unsynchronized.Id}", null);
                    }

                    var logoutUrl = $"https://api.naskban.ir/api/users/delsession?userId={loggedOnUser.User.Id}&sessionId={loggedOnUser.SessionId}";
                    await secureClient.DeleteAsync(logoutUrl);
                    return new RServiceResult<int>(unsynchronizeds.Length);
                }
            }
            catch (Exception exp)
            {
                return new RServiceResult<int>(0, exp.ToString());
            }
        }

        /// <summary>
        /// delete poem related naskban images by url
        /// </summary>
        /// <param name="naskbanUrl"></param>
        /// <returns></returns>
        public async Task<RServiceResult<bool>> DeletePoemRelatedNaskbanImagesByNaskbanUrlAsync(string naskbanUrl)
        {
            try
            {
                var images = await _context.PinterestLinks.Where(l => l.PinterestUrl == naskbanUrl && l.LinkType == LinkType.Naskban).ToListAsync();
                if (images.Count > 0)
                {
                    _context.RemoveRange(images);
                    await _context.SaveChangesAsync();
                }
                return new RServiceResult<bool>(true);
            }
            catch (Exception exp)
            {
                return new RServiceResult<bool>(false, exp.ToString());
            }
        }

        /// <summary>
        /// justify naskban links
        /// </summary>
        /// <param name="naskbanUserName"></param>
        /// <param name="naskbanPassword"></param>
        public void JustifyNaskbanPageNumbers(string naskbanUserName, string naskbanPassword)
        {
            _backgroundTaskQueue.QueueBackgroundWorkItem
                           (
                           async token =>
                           {
                               using (RMuseumDbContext context = new RMuseumDbContext(new DbContextOptions<RMuseumDbContext>())) //this is long running job, so _context might be already been freed/collected by GC
                               {
                                   LongRunningJobProgressServiceEF jobProgressServiceEF = new LongRunningJobProgressServiceEF(context);
                                   var job = (await jobProgressServiceEF.NewJob("JustifyNaskbanPageNumbers", "Query data")).Result;
                                   var res = await _JustifyNaskbanPageNumbersAsync(context, naskbanUserName, naskbanPassword, jobProgressServiceEF, job);
                                   if (!string.IsNullOrEmpty(res.ExceptionString))
                                   {
                                       await jobProgressServiceEF.UpdateJob(job.Id, 100, "", false, res.ExceptionString);
                                   }
                                   else
                                   {
                                       await jobProgressServiceEF.UpdateJob(job.Id, 100, "", true);
                                   }
                               }
                           });
        }

        private async Task<RServiceResult<bool>> _JustifyNaskbanPageNumbersAsync(RMuseumDbContext context, string naskbanUserName, string naskbanPassword, LongRunningJobProgressServiceEF jobProgressServiceEF, RLongRunningJobStatus job)
        {
            try
            {
                LoggedOnUserModelEx loggedOnUser;
                using (HttpClient client = new HttpClient())
                {
                    LoginViewModel loginViewModel = new LoginViewModel()
                    {
                        Username = naskbanUserName,
                        Password = naskbanPassword,
                        ClientAppName = "Ganjoor API",
                        Language = "fa-IR"
                    };
                    var loginResponse = await client.PostAsync("https://api.naskban.ir/api/users/login", new StringContent(JsonConvert.SerializeObject(loginViewModel), Encoding.UTF8, "application/json"));

                    if (loginResponse.StatusCode != HttpStatusCode.OK)
                    {
                        return new RServiceResult<bool>(false, "login error: " + JsonConvert.DeserializeObject<string>(await loginResponse.Content.ReadAsStringAsync()));
                    }
                    loggedOnUser = JsonConvert.DeserializeObject<LoggedOnUserModelEx>(await loginResponse.Content.ReadAsStringAsync());
                }


                using (HttpClient secureClient = new HttpClient())
                {
                    secureClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loggedOnUser.Token);


                    DateTime firstLinkSuggestionDate = DateTime.MinValue;
                    var optionName = "LastJustifiedNaskbanLinkSuggestionDate";
                    RGenericOption lastJustifiedNaskbanLinkPoemIdGenericOption = await (from o in context.Options.AsNoTracking()
                                                                                        where o.Name == optionName && o.RAppUserId == null
                                                                                        select o).SingleOrDefaultAsync();
                    if (lastJustifiedNaskbanLinkPoemIdGenericOption != null)
                    {
                        firstLinkSuggestionDate = DateTime.Parse(lastJustifiedNaskbanLinkPoemIdGenericOption.Value);
                    }
                    else
                    {
                        lastJustifiedNaskbanLinkPoemIdGenericOption = new RGenericOption()
                        {
                            Name = optionName,
                            Value = firstLinkSuggestionDate.ToString("yyyy-MM-dd HH:mm:ss"),
                        };
                        context.Add(lastJustifiedNaskbanLinkPoemIdGenericOption);
                        await context.SaveChangesAsync();
                    }

                    var naskbanLinks = await context.PinterestLinks.AsNoTracking().Where(l => l.LinkType == LinkType.Naskban && l.SuggestionDate > firstLinkSuggestionDate).OrderBy(l => l.SuggestionDate).ToListAsync();

                    if (firstLinkSuggestionDate == DateTime.MinValue && naskbanLinks.Any())
                    {
                        firstLinkSuggestionDate = naskbanLinks.First().SuggestionDate;
                    }
                    int progress = 0;
                    PDFBook book = null;
                    foreach (var naskbanLink in naskbanLinks)
                    {
                        progress++;
                        if (naskbanLink.SuggestionDate != firstLinkSuggestionDate)
                        {
                            var option = await context.Options.Where(o => o.Id == lastJustifiedNaskbanLinkPoemIdGenericOption.Id).SingleAsync();
                            option.Value = firstLinkSuggestionDate.ToString("yyyy-MM-dd HH:mm:ss");
                            context.Update(option);
                            await jobProgressServiceEF.UpdateJob(job.Id, progress, $"{progress} از {naskbanLinks.Count}");
                            firstLinkSuggestionDate = naskbanLink.SuggestionDate;
                        }
                        if (book == null || book.Id != naskbanLink.PDFBookId)
                        {
                            HttpResponseMessage responseBook = await secureClient.GetAsync($"https://api.naskban.ir/api/pdf/{naskbanLink.PDFBookId}?includePages=true&includeBookText=false&includePageText=true");
                            if (responseBook.StatusCode != HttpStatusCode.OK)
                            {
                                return new RServiceResult<bool>(false, $"book fetch error bookid = {naskbanLink.PDFBookId} naskbanlinkid = {naskbanLink.Id} - " + JsonConvert.DeserializeObject<string>(await responseBook.Content.ReadAsStringAsync()));
                            }
                            responseBook.EnsureSuccessStatusCode();

                            book = JsonConvert.DeserializeObject<PDFBook>(await responseBook.Content.ReadAsStringAsync());
                        }

                        string bookPage = book.Title;
                        if (!string.IsNullOrEmpty(book.AuthorsLine))
                        {
                            bookPage = $"{book.Title} - {book.AuthorsLine}";
                        }

                        var currentPage = book.Pages.Where(p => p.PageNumber == naskbanLink.PageNumber).Single();

                        var modifyNaskbanLink = await context.PinterestLinks.Where(l => l.Id == naskbanLink.Id).SingleAsync();
                        modifyNaskbanLink.AltText = $"{bookPage} - تصویر {naskbanLink.PageNumber.ToPersianNumbers()}";

                        var verses = await context.GanjoorVerses.AsNoTracking().Where(v => v.PoemId == naskbanLink.GanjoorPostId && v.VersePosition != VersePosition.Comment).OrderBy(v => v.VOrder).ToListAsync();
                        if (!verses.Any()) continue;
                        string verseText = verses.First().Text;
                        if (verses.Count > 1)
                        {
                            verseText += $" {verses[1].Text}";
                        }

                        string[] poemWords = verseText.Split(new char[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (poemWords.Length == 0) continue;

                        string pageText = currentPage.PageText;

                        bool firstVerseFound = false;

                        int found = 0;
                        foreach (var poemWord in poemWords)
                        {
                            if (pageText.Contains(poemWord))
                            {
                                found++;
                            }
                        }
                        var percentMainPage = found * 100 / poemWords.Length;

                        if (percentMainPage >= 70)
                        {
                            firstVerseFound = true;
                            modifyNaskbanLink.MatchPercent = percentMainPage;
                        }
                        else
                        {
                            int t = 0;
                            do
                            {
                                t++;
                                if ((naskbanLink.PageNumber + t) >= book.Pages.Count)
                                {
                                    break;
                                }

                                var nextPage = book.Pages.Where(p => p.PageNumber == naskbanLink.PageNumber + t).Single();
                                pageText = nextPage.PageText;

                                found = 0;
                                foreach (var poemWord in poemWords)
                                {
                                    if (pageText.Contains(poemWord))
                                    {
                                        found++;
                                    }
                                }
                                var percentNextPage = found * 100 / poemWords.Length;

                                if (percentNextPage >= 70)
                                {
                                    modifyNaskbanLink.PageNumber = nextPage.PageNumber;
                                    modifyNaskbanLink.PinterestImageUrl = nextPage.ExtenalThumbnailImageUrl;
                                    modifyNaskbanLink.PinterestUrl = $"https://naskban.ir/{nextPage.PDFBookId}/{nextPage.PageNumber}";
                                    modifyNaskbanLink.AltText = $"{bookPage} - تصویر {nextPage.PageNumber.ToPersianNumbers()}";
                                    modifyNaskbanLink.MatchPercent = percentNextPage;
                                    firstVerseFound = true;
                                    break;
                                }
                            }
                            while (t < 5);


                            if (!firstVerseFound && naskbanLink.PageNumber > 1)
                            {
                                var prevPage = book.Pages.Where(p => p.PageNumber == (naskbanLink.PageNumber - 1)).Single();
                                pageText = prevPage.PageText;

                                found = 0;
                                foreach (var poemWord in poemWords)
                                {
                                    if (pageText.Contains(poemWord))
                                    {
                                        found++;
                                    }
                                }
                                var percentPrevPage = found * 100 / poemWords.Length;

                                if (percentPrevPage >= 70)
                                {
                                    modifyNaskbanLink.PageNumber = prevPage.PageNumber;
                                    modifyNaskbanLink.PinterestImageUrl = prevPage.ExtenalThumbnailImageUrl;
                                    modifyNaskbanLink.PinterestUrl = $"https://naskban.ir/{prevPage.PDFBookId}/{prevPage.PageNumber}";
                                    modifyNaskbanLink.AltText = $"{bookPage} - تصویر {prevPage.PageNumber.ToPersianNumbers()}";
                                    modifyNaskbanLink.MatchPercent= percentPrevPage;
                                    firstVerseFound = true;
                                }
                            }
                        }

                        if(!firstVerseFound)
                        {
                            modifyNaskbanLink.ReviewResult = ReviewResult.Awaiting;
                        }

                        context.Update(modifyNaskbanLink);
                        await context.SaveChangesAsync();

                    }

                    var logoutUrl = $"https://api.naskban.ir/api/users/delsession?userId={loggedOnUser.User.Id}&sessionId={loggedOnUser.SessionId}";
                    await secureClient.DeleteAsync(logoutUrl);
                    return new RServiceResult<bool>(true);
                }
            }
            catch (Exception exp)
            {
                return new RServiceResult<bool>(false, exp.ToString());
            }
        }
    }
}