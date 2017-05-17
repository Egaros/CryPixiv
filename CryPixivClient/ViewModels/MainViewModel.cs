﻿using CryPixivClient.Objects;
using Pixeez.Objects;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;

namespace CryPixivClient.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public void Changed([CallerMemberName]string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #region Private fields
        ObservableCollection<PixivWork> foundWorks = new ObservableCollection<PixivWork>();

        string status = "Idle";
        string title = "CryPixiv";
        bool isWorking = false;
        string titleSuffix = "";
        List<PixivWork> dailyRankings = new List<PixivWork>();
        List<PixivWork> bookmarks = new List<PixivWork>();
        List<PixivWork> following = new List<PixivWork>();
        List<PixivWork> results = new List<PixivWork>();
        int currentPageResults = 1;
        string lastSearchQuery = null;
        int columns = 4;
        SynchronizationContext UIContext;
        #endregion

        #region Properties
        public ObservableCollection<PixivWork> FoundWorks
        {
            get => foundWorks;
            set { foundWorks = value; Changed(); }
        }

        public int CurrentPageResults { get => currentPageResults; set { currentPageResults = value; } }
        public string Status
        {
            get => status;
            set { status = value; Changed(); }
        }

        public bool IsWorking
        {
            get => isWorking;
            set { isWorking = value; Changed(); }
        }

        public string TitleSuffix
        {
            get => titleSuffix;
            set { titleSuffix = value; Changed("Title"); }
        }

        public string Title => title + (string.IsNullOrEmpty(titleSuffix) ? "" : " - " + titleSuffix);

        public int Columns => columns;

        public string LastSearchQuery => lastSearchQuery;
        #endregion

        public MainViewModel()
        {
            UIContext = SynchronizationContext.Current;         
        }

        public void UpdateColumns(double w)
        {
            if (w < 590) columns = 3;
            else if (w < 727) columns = 4;
            else if (w < 846) columns = 5;
            else if (w < 986) columns = 6;
            else columns = (int)Math.Floor(w / 140.0);

            Changed("Columns");
        }

        public async Task Show(List<PixivWork> cache, PixivAccount.WorkMode mode, string titleSuffix, string statusPrefix,
            Func<int, Task<List<PixivWork>>> getWorks, bool waitForUser = true)
        {
            // set starting values
            MainWindow.CurrentWorkMode = mode;
            MainWindow.DynamicWorksLimit = MainWindow.DefaultWorksLimit;

            // load cached results if they exist
            FoundWorks.Clear();
            int count = cache?.Count ?? 0;
            if (cache != null) FoundWorks.SwapCollection(cache);

            // show status
            TitleSuffix = titleSuffix;
            Status = $"{statusPrefix}...";    

            // start searching...
            await Task.Run(async () =>
            {
                var backupCache = cache.Copy();
                cache.Clear();
                bool first = false;
                int currentPage = 0;
                for (;;)
                {
                    if (MainWindow.CurrentWorkMode != mode) break;  // if user changes mode - break;

                    // if limit exceeded, stop downloading until user scrolls
                    if (MainWindow.DynamicWorksLimit < cache.Count && waitForUser)
                    {
                        Status = "Waiting for user to scroll to get more works... (" + FoundWorks.Count + " works displayed)";
                        IsWorking = false;
                        await Task.Delay(200);
                        continue;
                    }

                    try
                    {
                        // start downloading next page
                        IsWorking = true;
                        currentPage++;

                        // download current page
                        var works = await getWorks(currentPage);
                        if (works == null || MainWindow.CurrentWorkMode != mode) break;

                        // if cache has less entries than downloaded - swap cache with newest entries and keep updating...
                        if (cache.Count + works.Count > FoundWorks.Count)
                        {
                            if (first == false) UIContext.Send((a) => FoundWorks.AddToCollection(cache), null);

                            cache.AddRange(works);
                            UIContext.Send((a) => FoundWorks.AddList(works), null);
                            first = true;
                        }
                        else cache.AddRange(works);

                        Status = $"{statusPrefix}... " + cache.Count + " works" + ((FoundWorks.Count > cache.Count) ? $" (Displayed: {FoundWorks.Count} works from cache)" : "");
                    }
                    catch (Exception ex)
                    {
                        break;
                    }
                }

                if (cache.Count < backupCache.Count) cache.SwapList(backupCache);
                if (MainWindow.CurrentWorkMode == mode)
                {
                    IsWorking = false;
                    Status = "Done. (Found " + FoundWorks.Count + " works)";
                }
            });
        }
        public async void ShowSearch(string query, bool autosort = true, int continuePage = 1)
        {
            bool otherWasRunning = LastSearchQuery != query && query != null;

            if (query == null) query = LastSearchQuery;
            int maxResultCount = -1;
            lastSearchQuery = query;

            // cancel other running tasks
            while (queuedTasks.Count != 0)
            {
                var dq = queuedTasks.Dequeue();
                if (dq.IsCancellationRequested) continue;
                else dq.Cancel();
            }

            // set starting values
            var mode = PixivAccount.WorkMode.Search;
            MainWindow.CurrentWorkMode = mode;

            // load cached results if they exist
            FoundWorks.Clear();
            int count = results?.Count ?? 0;
            if (results != null && otherWasRunning == false) FoundWorks.SwapCollection(results);

            // show status
            TitleSuffix = "";
            Status = "Searching...";

            var csrc = new CancellationTokenSource();
            queuedTasks.Enqueue(csrc);

            // start searching...
            await Task.Run(async () =>
            {
                if (otherWasRunning) results.Clear();

                bool first = false;
                int currentPage = continuePage - 1;
                for (;;)
                {
                    if (MainWindow.CurrentWorkMode != mode || csrc.IsCancellationRequested) break; // if user changes mode or requests task to be cancelled - break;
                    // check if max results reached
                    if (maxResultCount != -1 && maxResultCount <= results.Count) break;

                    try
                    {
                        // start downloading next page
                        IsWorking = true;
                        currentPage++;

                        // download current page
                        var works = await MainWindow.Account.SearchWorks(query, currentPage);
                        if (works == null || MainWindow.CurrentWorkMode != mode || csrc.IsCancellationRequested) break;
                        if (maxResultCount == -1) maxResultCount = works.Pagination.Total ?? 0;

                        var wworks = works.ToPixivWork();
                        // if cache has less entries than downloaded - swap cache with newest entries and keep updating...
                        if (results.Count + works.Count > FoundWorks.Count || continuePage > 1)
                        {
                            if (first == false) UIContext.Send((a) => FoundWorks.AddToCollection(results), null);

                            results.AddToList(wworks);

                            UIContext.Send((a) =>
                            {
                                FoundWorks.AddToCollection(wworks);
                            }, null);

                            currentPageResults = currentPage;
                            first = true;
                        }
                        else results.AddRange(wworks);

                        Status = $"Searching... {FoundWorks.Count}/{maxResultCount} works";
                    }
                    catch (Exception ex)
                    {
                        break;
                    }
                }

                if (MainWindow.CurrentWorkMode == mode)
                {
                    IsWorking = false;
                    Status = "Done. (Found " + FoundWorks.Count + " works)";
                }
            }, csrc.Token);
        }

        public async void ShowDailyRankings() =>
            await Show(dailyRankings, PixivAccount.WorkMode.Ranking, "Daily Ranking", "Getting daily ranking", (page) => MainWindow.Account.GetDailyRanking(page));

        public async void ShowFollowing() =>
            await Show(following, PixivAccount.WorkMode.Following, "Following", "Getting following", (page) => MainWindow.Account.GetFollowing(page));

        public async void ShowBookmarks() =>
            await Show(bookmarks, PixivAccount.WorkMode.Bookmarks, "Bookmarks", "Getting bookmarks", (page) => MainWindow.Account.GetBookmarks(page));

        Queue<CancellationTokenSource> queuedTasks = new Queue<CancellationTokenSource>();
      
    }

    public static class Extensions
    {
        public static void SwapCollection<T>(this ObservableCollection<T> collection, IEnumerable<T> target)
        {
            collection.Clear();
            collection.AddList(target);
        }

        public static void AddToCollection(this ObservableCollection<PixivWork> collection, IEnumerable<PixivWork> target)
        {
            // add to collection, ignore existing ones
            foreach (var i in target)
            {
                if (collection.Count(x => x.Id == i.Id) > 0) continue;
                else collection.Add(i); 
            }
        }
        public static void AddToList(this List<PixivWork> collection, IEnumerable<PixivWork> target)
        {
            // add to collection, ignore existing ones
            foreach (var i in target)
            {
                if (collection.Count(x => x.Id == i.Id) > 0) continue;
                else collection.Add(i);
            }
        }

        public static void AddList<T>(this ObservableCollection<T> collection, IEnumerable<T> target)
        {
            foreach (var i in target) collection.Add(i);
        }

        public static List<PixivWork> ToPixivWork(this IEnumerable<Work> collection)
        {
            List<PixivWork> works = new List<PixivWork>();
            foreach (var w in collection) works.Add(new PixivWork(w));
            return works;
        }

        public static List<T> Copy<T>(this List<T> collection)
        {
            var lst = new List<T>();
            foreach (var l in collection) lst.Add(l);
            return lst;
        }

        public static void SwapList<T>(this List<T> source, List<T> target)
        {
            source.Clear();
            foreach (var l in target) source.Add(l);
        }
    }
}
