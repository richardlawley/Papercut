﻿// Papercut
// 
// Copyright © 2008 - 2012 Ken Robertson
// Copyright © 2013 - 2021 Jaben Cargman
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.


namespace Papercut.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Collections.Specialized;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reactive.Concurrency;
    using System.Reactive.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Data;
    using System.Windows.Forms;
    using System.Windows.Input;

    using Caliburn.Micro;

    using Papercut.Common.Domain;
    using Papercut.Common.Extensions;
    using Papercut.Common.Helper;
    using Papercut.Core.Annotations;
    using Papercut.Core.Domain.Message;
    using Papercut.Domain.Events;
    using Papercut.Domain.UiCommands;
    using Papercut.Domain.UiCommands.Commands;
    using Papercut.Helpers;
    using Papercut.Message;
    using Papercut.Message.Helpers;
    using Papercut.Properties;

    using Serilog;

    using Action = System.Action;
    using KeyEventArgs = System.Windows.Input.KeyEventArgs;
    using Screen = Caliburn.Micro.Screen;

    public class MessageListViewModel : Screen, IHandle<SettingsUpdatedEvent>
    {
        readonly object _deleteLockObject = new object();

        readonly ILogger _logger;

        readonly IMessageBus _messageBus;

        private readonly IUiCommandHub _uiCommandHub;

        readonly MessageRepository _messageRepository;

        readonly MessageWatcher _messageWatcher;

        readonly MimeMessageLoader _mimeMessageLoader;

        bool _isLoading;

        private int? _previousIndex;

        public MessageListViewModel(
            IUiCommandHub uiCommandHub,
            MessageRepository messageRepository,
            [NotNull] MessageWatcher messageWatcher,
            MimeMessageLoader mimeMessageLoader,
            IMessageBus messageBus,
            ILogger logger)
        {
            if (messageRepository == null)
                throw new ArgumentNullException(nameof(messageRepository));
            if (messageWatcher == null)
                throw new ArgumentNullException(nameof(messageWatcher));
            if (mimeMessageLoader == null)
                throw new ArgumentNullException(nameof(mimeMessageLoader));
            if (messageBus == null)
                throw new ArgumentNullException(nameof(messageBus));

            this._uiCommandHub = uiCommandHub;
            this._messageRepository = messageRepository;
            this._messageWatcher = messageWatcher;
            this._mimeMessageLoader = mimeMessageLoader;
            this._messageBus = messageBus;
            this._logger = logger;

            this.SetupMessages();
            this.RefreshMessageList();
        }

        public ObservableCollection<MimeMessageEntry> Messages { get; private set; }

        public ICollectionView MessagesSorted { get; private set; }

        public MimeMessageEntry SelectedMessage => this.GetSelected().FirstOrDefault();

        public string DeleteText => UIStrings.DeleteTextTemplate.RenderTemplate(this);

        public bool HasSelectedMessage => this.GetSelected().Any();

        public bool HasMessages => this.Messages.Any();

        public int SelectedMessageCount => this.GetSelected().Count();

        public bool IsLoading
        {
            get => this._isLoading;
            set
            {
                this._isLoading = value;
                this.NotifyOfPropertyChange(() => this.IsLoading);
            }
        }

        private ListSortDirection SortOrder => Enum.TryParse<ListSortDirection>(Settings.Default.MessageListSortOrder, out var sortOrder)
                                                   ? sortOrder
                                                   : ListSortDirection.Ascending;

        public Task HandleAsync(SettingsUpdatedEvent message, CancellationToken token)
        {
            this.MessagesSorted.SortDescriptions.Clear();
            this.MessagesSorted.SortDescriptions.Add(new SortDescription("ModifiedDate", this.SortOrder));

            return Task.CompletedTask;
        }

        MimeMessageEntry GetMessageByIndex(int index)
        {
            return this.MessagesSorted.OfType<MimeMessageEntry>().Skip(index).FirstOrDefault();
        }

        int? GetIndexOfMessage([CanBeNull] MessageEntry entry)
        {
            if (entry == null)
                return null;

            int index = this.MessagesSorted.OfType<MessageEntry>().FindIndex(m => Equals(entry, m));

            return index == -1 ? null : (int?)index;
        }

        void PushSelectedIndex()
        {
            if (this._previousIndex.HasValue)
            {
                return;
            }

            var selectedMessage = this.SelectedMessage;

            if (selectedMessage != null)
            {
                this._previousIndex = this.GetIndexOfMessage(selectedMessage);
            }
        }

        void PopSelectedIndex()
        {
            this._previousIndex = null;
        }

        void SetupMessages()
        {
            this.Messages = new ObservableCollection<MimeMessageEntry>();
            this.MessagesSorted = CollectionViewSource.GetDefaultView(this.Messages);

            this.MessagesSorted.SortDescriptions.Add(new SortDescription("ModifiedDate", this.SortOrder));

            // Begin listening for new messages
            this._messageWatcher.NewMessage += this.NewMessage;

            Observable.FromEventPattern(
                e => this._messageWatcher.RefreshNeeded += e,
                e => this._messageWatcher.RefreshNeeded -= e,
                TaskPoolScheduler.Default)
                .Throttle(TimeSpan.FromMilliseconds(100))
                .ObserveOnDispatcher()
                .Subscribe(e => this.RefreshMessageList());

            this.Messages.CollectionChanged += this.CollectionChanged;
        }

        void CollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            try
            {
                var notifyOfSelectionChange = new Action(
                    () =>
                    {
                        this.NotifyOfPropertyChange(() => this.HasSelectedMessage);
                        this.NotifyOfPropertyChange(() => this.SelectedMessageCount);
                        this.NotifyOfPropertyChange(() => this.SelectedMessage);
                        this.NotifyOfPropertyChange(() => this.DeleteText);
                    });

                if (args.NewItems != null)
                {
                    foreach (MimeMessageEntry m in args.NewItems.OfType<MimeMessageEntry>())
                    {
                        m.PropertyChanged += (o, eventArgs) => notifyOfSelectionChange();
                    }
                }

                notifyOfSelectionChange();
            }
            catch (Exception ex)
            {
                this._logger.Error(ex, "Failure Handling Message Collection Change {@Args}", args);
            }
        }

        void AddNewMessage(MessageEntry entry)
        {
            var observable = this._mimeMessageLoader.GetObservable(entry);

            observable.ObserveOnDispatcher().Subscribe(
                async message =>
                {
                    this._uiCommandHub.ShowBalloonTip(
                        3500,
                        "New Message Received",
                        $"From: {message.From.ToString().Truncate(50)}\r\nSubject: {message.Subject.Truncate(50)}",
                        ToolTipIcon.Info);

                    this.Messages.Add(new MimeMessageEntry(entry, this._mimeMessageLoader));

                    // handle selection if nothing is selected
                    this.ValidateSelected();
                },
                e =>
                {
                    // NOOP
                });
        }

        public int? TryGetValidSelectedIndex(int? previousIndex = null)
        {
            int messageCount = this.Messages.Count;

            if (messageCount == 0)
            {
                return null;
            }

            int? index = null;

            if (previousIndex.HasValue)
            {
                index = previousIndex;

                if (index >= messageCount)
                {
                    index = messageCount - 1;
                }
            }

            if (index <= 0 || index >= messageCount)
            {
                index = null;
            }

            // select the bottom
            if (!index.HasValue)
            {
                if (this.SortOrder == ListSortDirection.Ascending)
                {
                    index = messageCount - 1;
                }
                else
                {
                    index = 0;
                }
            }

            return index;
        }

        private void SetMessageByIndex(int index)
        {
            MimeMessageEntry m = this.GetMessageByIndex(index);
            if (m != null)
            {
                m.IsSelected = true;
            }
        }

        public void OpenMessageFolder()
        {
            string[] folders = this.GetSelected().Select(s => Path.GetDirectoryName(s.File)).Distinct().ToArray();
            folders.ForEach(f => Process.Start(f));
        }

        public void ValidateSelected()
        {
            if (this.SelectedMessageCount != 0 || this.Messages.Count == 0) return;

            var index = this.TryGetValidSelectedIndex(this._previousIndex);
            if (index.HasValue)
            {
                this.SetMessageByIndex(index.Value);
            }
        }

        void NewMessage(object sender, NewMessageEventArgs e)
        {
            Execute.OnUIThread(() => this.AddNewMessage(e.NewMessage));
        }

        public IEnumerable<MimeMessageEntry> GetSelected()
        {
            return this.Messages.Where(message => message.IsSelected);
        }

        public void ClearSelected()
        {
            foreach (MimeMessageEntry message in this.GetSelected().ToList())
            {
                message.IsSelected = false;
            }
        }

        public async Task DeleteAll()
        {
                this.ClearSelected();

                await this.DeleteMessagesAsync(this.Messages.ToList());
        }

        public async Task DeleteSelectedAsync()
        {
            // Lock to prevent rapid clicking issues
                this.PushSelectedIndex();

                var selectedMessageEntries = this.GetSelected().ToList();

                await this.DeleteMessagesAsync(selectedMessageEntries);
        }

        private async Task<List<string>> DeleteMessagesAsync(List<MimeMessageEntry> selectedMessageEntries)
        {
            List<string> failedEntries =
                selectedMessageEntries.Select(
                    entry =>
                    {
                        try
                        {
                            this._messageRepository.DeleteMessage(entry);
                            return null;
                        }
                        catch (Exception ex)
                        {
                            this._logger.Error(
                                ex,
                                "Failure Deleting Message {EmailMessageFile}",
                                entry.File);

                            return ex.Message;
                        }
                    }).Where(f => f != null).ToList();

            if (failedEntries.Any())
            {
                this._uiCommandHub.ShowMessage(
                    string.Join("\r\n", failedEntries),
                    $"Failed to Delete Message{(failedEntries.Count > 1 ? "s" : string.Empty)}");
            }

            return failedEntries;
        }

        public async Task MessageListKeyDown(KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            await this.DeleteSelectedAsync();
        }

        public void RefreshMessageList()
        {
            this.PushSelectedIndex();

            List<MessageEntry> messageEntries = this._messageRepository.LoadMessages()
                    .ToList();

            List<MimeMessageEntry> toAdd =
                messageEntries.Except(this.Messages)
                    .Select(m => new MimeMessageEntry(m, this._mimeMessageLoader))
                    .ToList();

            List<MimeMessageEntry> toDelete = this.Messages.Except(messageEntries).OfType<MimeMessageEntry>().ToList();
            toDelete.ForEach(m => this.Messages.Remove(m));

            this.Messages.AddRange(toAdd);

            this.MessagesSorted.Refresh();

            this.ValidateSelected();

            this.PopSelectedIndex();
        }
    }
}