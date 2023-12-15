﻿using DNTPersianUtils.Core;
using Microsoft.EntityFrameworkCore;
using RMuseum.DbContext;
using RMuseum.Models.Ganjoor;
using RSecurityBackend.Models.Generic;
using RSecurityBackend.Services.Implementation;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RMuseum.Services.Implementation
{
    /// <summary>
    /// IGanjoorService implementation
    /// </summary>
    public partial class GanjoorService : IGanjoorService
    {
        /// <summary>
        /// extracting quoted poems
        /// </summary>
        /// <returns></returns>
        public RServiceResult<bool> StartExtractingQuotedPoems()
        {
            try
            {
                _backgroundTaskQueue.QueueBackgroundWorkItem
                           (
                           async token =>
                           {
                               using (RMuseumDbContext context = new RMuseumDbContext(new DbContextOptions<RMuseumDbContext>())) //this is long running job, so _context might be already been freed/collected by GC
                               {
                                   LongRunningJobProgressServiceEF jobProgressServiceEF = new LongRunningJobProgressServiceEF(context);

                                   var job = (await jobProgressServiceEF.NewJob("StartExtractingQuotedPoems", "Query data")).Result;

                                   var pages = await context.GanjoorPages.AsNoTracking().Where(p => p.SecondPoetId != null).ToListAsync();
                                   foreach (var page in pages)
                                   {
                                       await jobProgressServiceEF.UpdateJob(job.Id, 0, page.FullTitle);
                                       var res = await _ParseRelatedPageAsync(context, page.Id);
                                       if(string.IsNullOrEmpty(res.ExceptionString))
                                       {
                                           await jobProgressServiceEF.UpdateJob(job.Id, 100, page.FullTitle, false, res.ExceptionString);
                                           return;
                                       }
                                       
                                   }
                                   await jobProgressServiceEF.UpdateJob(job.Id, 100, "", true);
                               }
                           });
                return new RServiceResult<bool>(true);
            }
            catch (Exception exp)
            {
                return new RServiceResult<bool>(false, exp.ToString());
            }
        }

        /// <summary>
        /// parse related pages
        /// </summary>
        /// <param name="context"></param>
        /// <param name="pageId"></param>
        /// <returns></returns>
        private async Task<RServiceResult<bool>> _ParseRelatedPageAsync(RMuseumDbContext context, int pageId)
        {
            try
            {
                var dbPage = await context.GanjoorPages.AsNoTracking().Where(p => p.Id == pageId).SingleAsync();
                int endIndex = dbPage.HtmlText.IndexOf("در بخش دوم");
                if(endIndex == -1)
                {
                    return new RServiceResult<bool>(false, "endIndex == -1");
                }
                int index = dbPage.HtmlText.IndexOf("<li>");
                while(index != -1 && index < endIndex)
                {
                    int closeTagIndex = dbPage.HtmlText.IndexOf("</li>", index);
                    int tagIndex = dbPage.HtmlText.IndexOf("\"", index);
                    string poem1Url = dbPage.HtmlText.Substring(tagIndex, dbPage.HtmlText.IndexOf("\"", index + 1) - tagIndex - 1 - 1 /*remove trailing slash*/ );
                    var poem1 = await context.GanjoorPoems.AsNoTracking().Where(p => p.FullUrl == poem1Url).SingleOrDefaultAsync();
                    if(poem1 == null)
                    {
                        var redirectedPage = await context.GanjoorPages.AsNoTracking().Where(p => p.RedirectFromFullUrl == poem1Url).SingleAsync();
                        poem1 = await context.GanjoorPoems.AsNoTracking().Where(p => p.Id == redirectedPage.Id).SingleAsync();
                    }
                    var poem1Cat = await context.GanjoorCategories.AsNoTracking().Where(c => c.Id == poem1.CatId).SingleAsync();
                    var poem1Poet = await context.GanjoorPoets.AsNoTracking().Where(p => p.Id == poem1Cat.PoetId).SingleAsync();

                    index += poem1Url.Length;
                    tagIndex = dbPage.HtmlText.IndexOf("\"", index);
                    string poem2Url = dbPage.HtmlText.Substring(tagIndex, dbPage.HtmlText.IndexOf("\"", index + 1) - tagIndex - 1 - 1 /*remove trailing slash*/ );
                    var poem2 = await context.GanjoorPoems.AsNoTracking().Where(p => p.FullUrl == poem2Url).SingleOrDefaultAsync();
                    if (poem2 == null)
                    {
                        var redirectedPage = await context.GanjoorPages.AsNoTracking().Where(p => p.RedirectFromFullUrl == poem2Url).SingleAsync();
                        poem2 = await context.GanjoorPoems.AsNoTracking().Where(p => p.Id == redirectedPage.Id).SingleAsync();
                    }

                    var poem2Cat = await context.GanjoorCategories.AsNoTracking().Where(c => c.Id == poem2.CatId).SingleAsync();
                    var poem2Poet = await context.GanjoorPoets.AsNoTracking().Where(p => p.Id == poem2Cat.PoetId).SingleAsync();

                    GanjoorQuotedPoem relatedPoem = new GanjoorQuotedPoem()
                    {
                        PoemId = poem1.Id,
                        RelatedPoemId = poem2.Id,
                        IsPriorToRelated = true,
                        ChosenForMainList = false == await context.GanjoorQuotedPoems.AsNoTracking().Where(p => p.PoemId == poem1.Id && p.CachedRelatedPoemPoetUrl == poem2Cat.FullUrl).AnyAsync(),
                        CachedRelatedPoemPoetDeathYearInLHijri = poem2Poet.DeathYearInLHijri,
                        CachedRelatedPoemPoetName = poem2Poet.Name,
                        CachedRelatedPoemPoetUrl = poem2Cat.FullUrl,
                        CachedRelatedPoemPoetImage =  $"/api/ganjoor/poet/image{poem2Cat.FullUrl}.gif",
                        CachedRelatedPoemFullTitle = poem2.FullTitle,
                        CachedRelatedPoemFullUrl = poem2.FullUrl,
                        SortOrder = 1000,
                        Note = "",
                        Published = true
                    };

                    //first couplet:
                    tagIndex = dbPage.HtmlText.IndexOf("<p>", tagIndex);
                    if(tagIndex != -1 && tagIndex < closeTagIndex)
                    {
                        tagIndex = dbPage.HtmlText.IndexOf("\">", tagIndex);
                        tagIndex += "\">".Length;
                        relatedPoem.CoupletIndex = -1 + int.Parse(PersianNumbersUtils.ToEnglishNumbers(dbPage.HtmlText.Substring(tagIndex, dbPage.HtmlText.IndexOf("<", tagIndex) - tagIndex - 1)));
                        
                        tagIndex = dbPage.HtmlText.IndexOf(":", tagIndex) + 1;

                        relatedPoem.CoupletVerse1 = dbPage.HtmlText.Substring(tagIndex, dbPage.HtmlText.IndexOf("-", tagIndex) - 1).Replace("\r", "").Replace("\n", "");
                        relatedPoem.CoupletVerse1ShouldBeEmphasized = false;
                        if (relatedPoem.CoupletVerse1.Contains("strong"))
                        {
                            relatedPoem.CoupletVerse1ShouldBeEmphasized = true;
                            relatedPoem.CoupletVerse1 = relatedPoem.CoupletVerse1.Replace("<strong>", "").Replace("</strong>", "");
                        }

                        relatedPoem.CoupletVerse2 = dbPage.HtmlText.Substring(dbPage.HtmlText.IndexOf("-", tagIndex), dbPage.HtmlText.IndexOf("</p>", tagIndex) - 1).Replace("\r", "").Replace("\n", "");
                        relatedPoem.CoupletVerse2ShouldBeEmphasized = false;
                        if (relatedPoem.CoupletVerse2.Contains("strong"))
                        {
                            relatedPoem.CoupletVerse2ShouldBeEmphasized = true;
                            relatedPoem.CoupletVerse2 = relatedPoem.CoupletVerse2.Replace("<strong>", "").Replace("</strong>", "");
                        }
                    }

                    tagIndex = dbPage.HtmlText.IndexOf("<p>", tagIndex);
                    if (tagIndex != -1 && tagIndex < closeTagIndex)
                    {
                        tagIndex = dbPage.HtmlText.IndexOf("\">", tagIndex);
                        tagIndex += "\">".Length;
                        relatedPoem.RelatedCoupletIndex = -1 + int.Parse(PersianNumbersUtils.ToEnglishNumbers(dbPage.HtmlText.Substring(tagIndex, dbPage.HtmlText.IndexOf("<", tagIndex) - tagIndex - 1)));

                        tagIndex = dbPage.HtmlText.IndexOf(":", tagIndex) + 1;

                        relatedPoem.RelatedCoupletVerse1 = dbPage.HtmlText.Substring(tagIndex, dbPage.HtmlText.IndexOf("-", tagIndex) - 1).Replace("\r", "").Replace("\n", "");
                        relatedPoem.RelatedCoupletVerse1ShouldBeEmphasized = false;
                        if (relatedPoem.RelatedCoupletVerse1.Contains("strong"))
                        {
                            relatedPoem.RelatedCoupletVerse1ShouldBeEmphasized = true;
                            relatedPoem.RelatedCoupletVerse1 = relatedPoem.RelatedCoupletVerse1.Replace("<strong>", "").Replace("</strong>", "");
                        }

                        relatedPoem.RelatedCoupletVerse2 = dbPage.HtmlText.Substring(dbPage.HtmlText.IndexOf("-", tagIndex), dbPage.HtmlText.IndexOf("</p>", tagIndex) - 1).Replace("\r", "").Replace("\n", "");
                        relatedPoem.RelatedCoupletVerse2ShouldBeEmphasized = false;
                        if (relatedPoem.RelatedCoupletVerse2.Contains("strong"))
                        {
                            relatedPoem.RelatedCoupletVerse2ShouldBeEmphasized = true;
                            relatedPoem.RelatedCoupletVerse2 = relatedPoem.RelatedCoupletVerse2.Replace("<strong>", "").Replace("</strong>", "");
                        }
                    }



                    context.Add(relatedPoem);
                    await context.SaveChangesAsync();


                    GanjoorQuotedPoem reverseRelation = new GanjoorQuotedPoem()
                    {
                        PoemId = poem2.Id,
                        RelatedPoemId = poem1.Id,
                        IsPriorToRelated = false,
                        ChosenForMainList = false == await context.GanjoorQuotedPoems.AsNoTracking().Where(p => p.PoemId == poem2.Id && p.CachedRelatedPoemPoetUrl == poem1Cat.FullUrl).AnyAsync(),
                        CachedRelatedPoemPoetDeathYearInLHijri = poem1Poet.DeathYearInLHijri,
                        CachedRelatedPoemPoetName = poem1Poet.Name,
                        CachedRelatedPoemPoetUrl = poem1Cat.FullUrl,
                        CachedRelatedPoemPoetImage = $"/api/ganjoor/poet/image{poem1Cat.FullUrl}.gif",
                        CachedRelatedPoemFullTitle = poem1.FullTitle,
                        CachedRelatedPoemFullUrl = poem1.FullUrl,
                        SortOrder = 1000,
                        Note = "",
                        Published = true,
                        CoupletVerse1 = relatedPoem.CoupletVerse1,
                        CoupletVerse1ShouldBeEmphasized = relatedPoem.CoupletVerse1ShouldBeEmphasized,
                        CoupletVerse2 = relatedPoem.CoupletVerse2,
                        CoupletVerse2ShouldBeEmphasized = relatedPoem.CoupletVerse2ShouldBeEmphasized,
                        CoupletIndex = relatedPoem.CoupletIndex,
                        RelatedCoupletVerse1 = relatedPoem.RelatedCoupletVerse1,
                        RelatedCoupletVerse1ShouldBeEmphasized = relatedPoem.RelatedCoupletVerse1ShouldBeEmphasized,
                        RelatedCoupletVerse2 = relatedPoem.RelatedCoupletVerse2,
                        RelatedCoupletVerse2ShouldBeEmphasized = relatedPoem.RelatedCoupletVerse2ShouldBeEmphasized,
                        RelatedCoupletIndex = relatedPoem.RelatedCoupletIndex,
                    };
                    context.Add(reverseRelation);
                    await context.SaveChangesAsync();

                    index = dbPage.HtmlText.IndexOf("<li>", index);
                }
                return new RServiceResult<bool>(true);
            }
            catch (Exception exp)
            {
                return new RServiceResult<bool>(false, exp.ToString());
            }
        }
    }
}
