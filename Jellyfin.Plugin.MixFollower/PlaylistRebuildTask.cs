﻿// <copyright file="PlaylistRebuildTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MixFollower
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CliWrap;
    using CliWrap.Buffered;
    using Jellyfin.Data.Entities;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities.Audio;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Playlists;

    using MediaBrowser.Model.Globalization;
    using MediaBrowser.Model.Playlists;
    using MediaBrowser.Model.Search;
    using MediaBrowser.Model.Tasks;

    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Deletes old log files.
    /// </summary>
    public class PlaylistRebuildTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly Guid firstAdminId;

        private readonly User firstAdmin;
        private readonly ILibraryManager libraryManager;
        private readonly IPlaylistManager playlistManager;
        private readonly IUserManager userManager;
        private readonly ISearchEngine searchEngine;

        private readonly ILocalizationManager localization;
        private readonly ILogger<PlaylistRebuildTask> logger;

        private readonly MetaDb db;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaylistRebuildTask" /> class.
        /// </summary>
        /// <param name="userManager">The user manager.</param>
        /// <param name="libraryManager">The configuration manager.</param>
        /// <param name="playlistManager">The playlist manager.</param>
        /// <param name="logger">The  logger.</param>
        /// <param name="localization">The localization manager.</param>
        public PlaylistRebuildTask(ILibraryManager libraryManager, ISearchEngine searchEngine, IUserManager userManager, IPlaylistManager playlistManager, ILogger<PlaylistRebuildTask> logger, ILocalizationManager localization)
        {
            this.userManager = userManager;
            this.playlistManager = playlistManager;
            this.libraryManager = libraryManager;
            this.logger = logger;
            this.localization = localization;
            this.searchEngine = searchEngine;
            this.firstAdmin = this.userManager.Users.First(i => i.HasPermission(PermissionKind.IsAdministrator));
            this.firstAdminId = this.firstAdmin.Id;
            this.db = new MetaDb(this.libraryManager);

            this.logger.LogInformation("PlaylistRebuildTask constructed");
        }

        /// <inheritdoc />
        public string Name => this.localization.GetLocalizedString("TaskPlaylistRebuild");

        /// <inheritdoc />
        public string Description => string.Format(
            CultureInfo.InvariantCulture,
            this.localization.GetLocalizedString("TaskPlaylistRebuildDescription"));

        /// <inheritdoc />
        public string Category => this.localization.GetLocalizedString("TasksMaintenanceCategory");

        /// <inheritdoc />
        public string Key => "PlaylistRebuild";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <summary>
        /// Creates the triggers that define when the task will run.
        /// </summary>
        /// <returns>IEnumerable{BaseTaskTrigger}.</returns>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            this.logger.LogInformation("PlaylistRebuild GetDefaultTriggers");
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup },

                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(24).Ticks },
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            this.logger.LogInformation("ExecuteAsync");
            cancellationToken.ThrowIfCancellationRequested();

            var commands_to_fetch = Plugin.Instance.Configuration.CommandsToFetch;

            this.logger.LogInformation("commands_to_fetch size : {size}", commands_to_fetch.Count);

            foreach (var command in commands_to_fetch)
            {
                this.logger.LogInformation("command {Command} Executing", command);
                await this.CreatePlaylistFromFetchCommand(this.firstAdminId, command).ConfigureAwait(false);
            }
        }

        private void DeletePlaylist(string playlist_name)
        {
            var playlists = this.playlistManager.GetPlaylists(this.firstAdminId);
            var playlist = playlists.FirstOrDefault(playlist => playlist.Name == playlist_name);
            if (playlist is null)
            {
                return;
            }

            this.libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
            this.logger.LogInformation("matched and deleted {PN}", playlist_name);
        }

        private async Task<Audio?> GetMostMatchedSongFromJObject(JObject jobject, Func<string, string, Task<Audio?>>? action)
        {
            var title = jobject?.GetValue("title")?.ToString()!;
            var artist = jobject?.GetValue("artist")?.ToString()!;

            var item = this.GetMostMatchedSong(title, artist);
            if (item is null && action is not null)
            {
                item = await action(title, artist).ConfigureAwait(false);
            }

            return item;
        }

        private async Task<string> CreatePlaylistFromFetchCommand(Guid user, string command)
        {
            this.logger.LogInformation("cli command executing {Command}", command);
            CliWrap.Buffered.BufferedCommandResult? result = null;
            try
            {
                result = await Cli.Wrap(command).ExecuteBufferedAsync().ConfigureAwait(false);

                // result.ToString();
                var json = result.StandardOutput.ToString();

                var obj = JObject.Parse(json);

                var playlist_name = obj?.GetValue("name")?.ToString()!;

                var songs = obj?.GetValue("songs");
                this.db.RecreateDb();
                var list_items = new List<Guid>();
                songs?.Children<JObject>()
                .ToList()
                .ForEach(async jobject => await this.GetMostMatchedSongFromJObject(jobject, this.DownloadMusic).ConfigureAwait(false));

                // _iLibraryMonitor.ReportFileSystemChangeComplete(path, false);
                await this.libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                this.DeletePlaylist(playlist_name);
                this.db.RecreateDb();

                songs?.Children<JObject>()
                .ToList()
                .ForEach(async jobject =>
                {
                    var item = await this.GetMostMatchedSongFromJObject(jobject, null).ConfigureAwait(false);
                    if (item is not null)
                    {
                        list_items.Add(item.Id);
                    }
                });

                var playlist = await this.playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = playlist_name,
                    ItemIdList = list_items,
                    UserId = user,
                    MediaType = Data.Enums.MediaType.Audio,

                    // Users = [],
                    Public = true,
                }).ConfigureAwait(false);

                return playlist.Id;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                this.logger.LogInformation("executing {command} gets crash! {msg} ", command, exception.Message);
                this.logger.LogInformation("{stack_trace}", exception.StackTrace?.ToString());
            }

            return string.Empty;
        }

        private bool SubstrMetric(Audio? song, string[] tokenized_artist)
        {
            if (song is null)
            {
                return false;
            }

            bool ContainsToken(string a)
            {
                return tokenized_artist.Any(token => a.Contains(token, StringComparison.InvariantCulture) || a.Contains(token, StringComparison.InvariantCulture));
            }

            var result = song.Artists.Any(ContainsToken);
            if (!result)
            {
                var join = string.Join(' ', tokenized_artist);
                this.logger.LogInformation("I want artist {Joined}", join);
                this.logger.LogInformation("song artists...");
                song.Artists.ToList().ForEach(a => this.logger.LogInformation("Artist : {A}", a));
            }

            return result;
        }

        private Audio? GetMostMatchedSong(string title, string artist)
        {
            this.logger.LogInformation("Querying with {Query}...", title);
            var tokenized_artist = artist.Split(['(', ' ', ')']);
            MediaType[] audioTypes = [MediaType.Audio];
            var hints = this.searchEngine.GetSearchHints(new SearchQuery()
            {
                MediaTypes = audioTypes,
                SearchTerm = title,
            });

            var song = hints.Items
            .Select(hint => hint.Item is Audio song ? song : null)
            .Where(song => this.SubstrMetric(song, tokenized_artist))
            .FirstOrDefault();

            if (song is null)
            {
                var result = this.db.SearchByFilename(title);
                if (result.Count() != 1)
                {
                    this.logger.LogInformation("# of query results : {Count} ({Title}, {Artist})", result.Count(), title, artist);
                }

                song = result.Select(item => item is Audio song ? song : null)

                // .Where(song => this.SubstrMetric(song, tokenized_artist)) // we have to solve beyonce problem
                .FirstOrDefault();
                if (song is null)
                {
                    this.logger.LogInformation("even LibrarySearch failed with artist {Artist}...", artist);
                }
            }

            return song;
        }

        private static async Task<bool> DownloadMusicFromSource(string source, string title, string artist)
        {
            if (source.StartsWith("https"))
            {
                return false;
            }

            var interpolated = source.Replace("${title}", "\"" + title + "\"")
                                     .Replace("${artist}", "\"" + artist + "\"");
            var cmd = interpolated.Split(' ', 2);

            var result = await Cli.Wrap(cmd[0])
            .WithArguments(cmd[1])
            .ExecuteBufferedAsync()
            .ConfigureAwait(false);
            return result.IsSuccess;
        }

        private async Task<Audio?> DownloadMusic(string title, string artist)
        {
            var methods_to_download = Plugin.Instance.Configuration.ApisDownload;
            foreach (var source in methods_to_download)
            {
                try
                {
                    var success = await DownloadMusicFromSource(source, title, artist).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    this.logger.LogInformation("download from {Source} failed  {Msg}", source, e.Message);
                }
            }

            return null;
        }
    }
}
