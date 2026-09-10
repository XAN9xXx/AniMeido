using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.AI.Models;
using AniMeido.Plugin.AI.Providers;
using AniMeido.Plugin.AI.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AniMeido.Plugin.AI.Views;

internal sealed class AiWorkbenchWindow : Window
{
    private readonly AiTaskCoordinator _coordinator;
    private readonly AiPluginPaths _paths;
    private readonly ListView _conversationList = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly StackPanel _messagePanel = new() { Spacing = 18 };
    private readonly StackPanel _proposalPanel = new() { Spacing = 8 };
    private readonly StackPanel _contextItems = new() { Spacing = 4 };
    private readonly TextBox _search = new();
    private readonly TextBox _prompt = new();
    private readonly TextBlock _conversationTitle = new()
    {
        Text = "选择一项 AI 任务",
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
    };
    private readonly TextBlock _conversationMeta = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.65,
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.72,
    };
    private readonly TextBlock _snapshotSummary = new()
    {
        Text = "尚未配置任务数据。",
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly TextBlock _catalogCount = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.65,
    };
    private readonly ScrollViewer _messageScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly SplitView _workspaceSplit = new()
    {
        DisplayMode = SplitViewDisplayMode.Overlay,
        PanePlacement = SplitViewPanePlacement.Right,
        OpenPaneLength = 380,
        CompactPaneLength = 0,
        IsPaneOpen = false,
    };
    private readonly Button _sendButton = new()
    {
        Content = "发送",
        Style = Application.Current.Resources["AccentButtonStyle"] as Style,
    };
    private readonly Button _cancelButton = new()
    {
        Content = "停止",
        IsEnabled = false,
    };
    private readonly Button _editContextButton = new()
    {
        Content = "编辑上下文",
    };
    private readonly Button _inspectorButton = new()
    {
        Content = "任务详情",
    };
    private readonly Button _applyButton = new()
    {
        Content = "审查并应用",
        IsEnabled = false,
    };
    private readonly List<PersonalAnimeSelectionItem> _animeCatalog = [];
    private CancellationTokenSource? _sendCancellation;
    private AiConversation? _currentConversation;
    private PersonalAnimeContextSnapshot? _currentSnapshot;
    private bool _loaded;
    private bool _isSending;
    private bool _suppressConversationSelection;

    public AiWorkbenchWindow(AiTaskCoordinator coordinator, AiPluginPaths paths)
    {
        _coordinator = coordinator;
        _paths = paths;
        Title = "AniMeido AI 工作台";
        DpiWindowSizing.Resize(this, 1480, 900);
        Content = BuildContent();
        Activated += OnActivated;
        Closed += OnClosed;
    }

    private UIElement BuildContent()
    {
        var root = new Grid
        {
            Background = Application.Current.Resources[
                "ApplicationPageBackgroundThemeBrush"] as Brush,
        };
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(270),
        });
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        root.Children.Add(BuildConversationRail());
        _workspaceSplit.Content = BuildConversationWorkspace();
        _workspaceSplit.Pane = BuildInspector();
        Grid.SetColumn(_workspaceSplit, 1);
        root.Children.Add(_workspaceSplit);
        return root;
    }

    private FrameworkElement BuildConversationRail()
    {
        var create = new Button
        {
            Content = "新建任务",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
        };
        create.Click += OnShowTaskHub;
        _search.PlaceholderText = "搜索任务记录";
        _search.TextChanged += OnSearchChanged;
        _conversationList.SelectionChanged += OnConversationSelected;

        var more = new Button
        {
            Content = "任务记录管理",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("重命名当前任务记录", OnRenameConversation));
        menu.Items.Add(MenuItem("删除当前任务记录", OnDeleteConversation));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("导出全部任务记录", OnExport));
        menu.Items.Add(MenuItem("导入最近的导出文件", OnImport));
        more.Flyout = menu;

        var grid = new Grid
        {
            Padding = new Thickness(14, 16, 12, 14),
            Background = Application.Current.Resources[
                "LayerFillColorDefaultBrush"] as Brush,
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock
        {
            Text = "AniMeido AI",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        title.Children.Add(new TextBlock
        {
            Text = "AI 任务与历史记录",
            Opacity = 0.62,
        });
        grid.Children.Add(title);
        Grid.SetRow(create, 1);
        create.Margin = new Thickness(0, 14, 0, 8);
        grid.Children.Add(create);
        Grid.SetRow(_search, 2);
        grid.Children.Add(_search);
        Grid.SetRow(_conversationList, 3);
        _conversationList.Margin = new Thickness(0, 10, 0, 10);
        grid.Children.Add(_conversationList);
        Grid.SetRow(more, 4);
        grid.Children.Add(more);
        return grid;
    }

    private FrameworkElement BuildConversationWorkspace()
    {
        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.PlaceholderText = "给 AniMeido AI 发送消息…";
        _prompt.MinHeight = 56;
        _prompt.MaxHeight = 160;
        _sendButton.Click += OnSend;
        _cancelButton.Click += (_, _) => _sendCancellation?.Cancel();
        _editContextButton.Click += OnEditContext;
        _inspectorButton.Click += (_, _) =>
            _workspaceSplit.IsPaneOpen = !_workspaceSplit.IsPaneOpen;

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerActions.Children.Add(_editContextButton);
        headerActions.Children.Add(_inspectorButton);

        var header = new Grid
        {
            Padding = new Thickness(26, 18, 26, 14),
            BorderBrush = Application.Current.Resources[
                "CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(_conversationTitle);
        heading.Children.Add(_conversationMeta);
        heading.Children.Add(_status);
        header.Children.Add(heading);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);

        var messageHost = new Grid { Padding = new Thickness(30, 24, 30, 30) };
        _messagePanel.MaxWidth = 860;
        _messagePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        messageHost.Children.Add(_messagePanel);
        _messageScroll.Content = messageHost;
        ShowEmptyConversation();

        var composerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        composerButtons.Children.Add(_cancelButton);
        composerButtons.Children.Add(_sendButton);
        var composer = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(18, 12, 18, 18),
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        composer.Children.Add(_prompt);
        composer.Children.Add(new Grid
        {
            Children =
            {
                new TextBlock
                {
                    Text = "发送前会预览任务数据与追问历史",
                    Opacity = 0.58,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                composerButtons,
            },
        });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(header);
        Grid.SetRow(_messageScroll, 1);
        grid.Children.Add(_messageScroll);
        Grid.SetRow(composer, 2);
        grid.Children.Add(composer);
        return grid;
    }

    private FrameworkElement BuildInspector()
    {
        _applyButton.Click += OnApplyProposals;
        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
        };
        ToolTipService.SetToolTip(close, "关闭");
        close.Click += (_, _) => _workspaceSplit.IsPaneOpen = false;
        var editContext = new Button
        {
            Content = "编辑上下文",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        editContext.Click += OnEditContext;
        ShowProposals([]);

        var panel = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(20, 18, 20, 20),
        };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "任务详情",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
        });
        Grid.SetColumn(close, 1);
        heading.Children.Add(close);
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = "发送给模型的数据",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        });
        panel.Children.Add(_snapshotSummary);
        panel.Children.Add(_catalogCount);
        panel.Children.Add(_contextItems);
        panel.Children.Add(editContext);
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 12, 0, 8),
            Background = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "变更提案",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "模型只能提出变更；只有勾选并确认后才会写入 AniMeido。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.62,
        });
        panel.Children.Add(_proposalPanel);
        panel.Children.Add(_applyButton);
        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Application.Current.Resources[
                "LayerFillColorDefaultBrush"] as Brush,
            BorderBrush = Application.Current.Resources[
                "CardStrokeColorDefaultBrush"] as Brush,
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
    }

    private static MenuFlyoutItem MenuItem(
        string text,
        RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RunUiAsync(async () =>
        {
            await ReloadAnimeCatalogAsync();
            await ReloadConversationsAsync();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _sendCancellation?.Cancel();
        _sendCancellation?.Dispose();
    }

    private async void OnCreateTask(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: AiTaskDefinition selected })
        {
            return;
        }

        await RunUiAsync(() => CreateConversationAsync(selected));
    }

    private void OnShowTaskHub(object sender, RoutedEventArgs args)
    {
        _suppressConversationSelection = true;
        _conversationList.SelectedItem = null;
        _suppressConversationSelection = false;
        _currentConversation = null;
        _currentSnapshot = null;
        _workspaceSplit.IsPaneOpen = false;
        ShowEmptyConversation();
        ShowProposals([]);
        UpdateContextPresentation();
        UpdateCommandState();
    }

    private async Task CreateConversationAsync(AiTaskDefinition selected)
    {
        var conversation = await _coordinator.CreateConversationAsync(selected.Kind);
        _currentConversation = conversation;
        _currentSnapshot = null;
        await ReloadConversationsAsync(conversation.ConversationId);
        await ConfigureContextAsync(showCancelHint: true);
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs args)
        => await RunUiAsync(() => ReloadConversationsAsync(
            _currentConversation?.ConversationId));

    private async void OnConversationSelected(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_suppressConversationSelection)
        {
            return;
        }

        if (_conversationList.SelectedItem is ListViewItem
            {
                Tag: AiConversation item,
            })
        {
            await RunUiAsync(() => LoadConversationAsync(item));
        }
    }

    private async Task LoadConversationAsync(AiConversation conversation)
    {
        _workspaceSplit.IsPaneOpen = false;
        _currentConversation = conversation;
        _currentSnapshot = AiTaskCoordinator.ReadSnapshot(conversation);
        var messages = await _coordinator.GetMessagesAsync(
            conversation.ConversationId);
        _conversationTitle.Text = GetTaskTitle(conversation.TaskKind);
        _conversationMeta.Text =
            $"任务记录：{conversation.Title} · {conversation.Provider} / {conversation.Model}";
        _status.Text = messages.Count == 0
            ? "新任务"
            : $"{messages.Count(message => message.Role == "user")} 个对话轮次";
        _messagePanel.Children.Clear();
        if (messages.Count == 0)
        {
            ShowEmptyConversation(
                "任务数据准备好后，就可以围绕当前目标连续追问。",
                "所有发送仍会经过授权预览。",
                showTaskChoices: false);
        }
        else
        {
            IReadOnlyList<PersonalAnimeChange> latestProposals = [];
            foreach (var message in messages)
            {
                AddMessage(message.Role, message.Body);
                var proposals = AiTaskCoordinator.ReadProposedChanges(message);
                if (proposals.Count > 0)
                {
                    latestProposals = proposals;
                }
            }

            ShowProposals(latestProposals);
        }

        UpdateContextPresentation();
        UpdateCommandState();
        ScrollToBottom();
    }

    private async void OnEditContext(object sender, RoutedEventArgs args)
        => await RunUiAsync(() => ConfigureContextAsync(showCancelHint: false));

    private async Task ConfigureContextAsync(bool showCancelHint)
    {
        if (_currentConversation is null)
        {
            _status.Text = "请先新建或选择任务。";
            return;
        }

        if (_animeCatalog.Count == 0)
        {
            await ReloadAnimeCatalogAsync();
        }

        var definition = _coordinator.TaskDefinitions.Single(
            item => item.Kind == _currentConversation.TaskKind);
        var selectedIds = (_currentSnapshot?.Items ?? [])
            .Select(item => item.AnimeId)
            .ToHashSet();
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 390,
        };
        var search = new TextBox { PlaceholderText = "筛选可选番剧" };
        var validation = new TextBlock
        {
            Text = SelectionRequirement(definition),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
        };
        var rebuilding = false;
        void RebuildList()
        {
            rebuilding = true;
            list.Items.Clear();
            var query = search.Text.Trim();
            foreach (var item in _animeCatalog.Where(item =>
                query.Length == 0
                || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            {
                var view = new ListViewItem
                {
                    Content = BuildAnimeSelectionContent(item),
                    Tag = item,
                };
                list.Items.Add(view);
                if (selectedIds.Contains(item.AnimeId))
                {
                    list.SelectedItems.Add(view);
                }
            }

            rebuilding = false;
        }

        list.SelectionChanged += (_, args) =>
        {
            if (rebuilding)
            {
                return;
            }

            foreach (var added in args.AddedItems.OfType<ListViewItem>())
            {
                selectedIds.Add(((PersonalAnimeSelectionItem)added.Tag).AnimeId);
            }

            foreach (var removed in args.RemovedItems.OfType<ListViewItem>())
            {
                selectedIds.Remove(((PersonalAnimeSelectionItem)removed.Tag).AnimeId);
            }

            validation.Text =
                $"已选择 {selectedIds.Count} 项 · {SelectionRequirement(definition)}";
        };
        search.TextChanged += (_, _) => RebuildList();
        RebuildList();

        var refresh = new Button { Content = "从 AniMeido 刷新列表" };
        refresh.Click += async (_, _) => await RunUiAsync(async () =>
        {
            await ReloadAnimeCatalogAsync();
            RebuildList();
        });
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "只把与当前任务有关的条目加入授权数据。保存后生成新的冻结快照。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(search);
        content.Children.Add(list);
        content.Children.Add(validation);
        content.Children.Add(refresh);
        var dialog = CreateDialog(
            "编辑任务数据",
            content,
            "保存上下文",
            "取消");
        dialog.Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary)
            {
                return;
            }

            if (selectedIds.Count < definition.MinimumAnimeCount
                || selectedIds.Count > definition.MaximumAnimeCount)
            {
                args.Cancel = true;
                validation.Text =
                    $"选择数量不符合要求：{SelectionRequirement(definition)}";
                validation.Foreground = Application.Current.Resources[
                    "SystemFillColorCriticalBrush"] as Brush;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            if (showCancelHint && _currentSnapshot is null)
            {
                _status.Text = "任务已创建；配置授权数据后即可开始。";
            }

            return;
        }

        _currentSnapshot = await _coordinator.RefreshSnapshotAsync(
            _currentConversation,
            selectedIds.Order().ToList());
        _currentConversation = (await _coordinator.GetConversationsAsync())
            .First(item => item.ConversationId == _currentConversation.ConversationId);
        _status.Text = "任务授权数据已更新。";
        UpdateContextPresentation();
        UpdateCommandState();
        await ReloadConversationsAsync(_currentConversation.ConversationId);
    }

    private async void OnRenameConversation(object sender, RoutedEventArgs args)
        => await RunUiAsync(RenameConversationAsync);

    private async Task RenameConversationAsync()
    {
        if (_currentConversation is null)
        {
            _status.Text = "请先选择任务记录。";
            return;
        }

        var input = new TextBox { Text = _currentConversation.Title };
        var dialog = CreateDialog("重命名任务记录", input, "保存", "取消");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && !string.IsNullOrWhiteSpace(input.Text))
        {
            await _coordinator.RenameConversationAsync(
                _currentConversation.ConversationId,
                input.Text);
            await ReloadConversationsAsync(_currentConversation.ConversationId);
        }
    }

    private async void OnDeleteConversation(object sender, RoutedEventArgs args)
        => await RunUiAsync(DeleteConversationAsync);

    private async Task DeleteConversationAsync()
    {
        if (_currentConversation is null)
        {
            _status.Text = "请先选择任务记录。";
            return;
        }

        var dialog = CreateDialog(
            "删除任务记录？",
            "只删除 AI 插件本地历史，不影响 AniMeido 中的番剧、计划或档案。",
            "删除",
            "取消");
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _coordinator.DeleteConversationAsync(
            _currentConversation.ConversationId);
        _currentConversation = null;
        _currentSnapshot = null;
        ShowEmptyConversation();
        ShowProposals([]);
        UpdateContextPresentation();
        await ReloadConversationsAsync();
    }

    private async void OnSend(object sender, RoutedEventArgs args)
        => await RunUiAsync(SendCoreAsync);

    private async Task SendCoreAsync()
    {
        if (_isSending)
        {
            return;
        }

        if (_currentConversation is null)
        {
            _status.Text = "请先新建或选择任务。";
            return;
        }

        if (_currentSnapshot is null)
        {
            _status.Text = "请先为此任务配置授权数据。";
            await ConfigureContextAsync(showCancelHint: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_prompt.Text))
        {
            _status.Text = "请输入消息。";
            return;
        }

        var settings = await _coordinator.GetSettingsAsync();
        var preview = await _coordinator.BuildAuthorizationPreviewAsync(
            _currentConversation,
            _currentSnapshot,
            _prompt.Text,
            settings.AllowProviderWebTools);
        var previewBox = new TextBox
        {
            Text = preview,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 460,
        };
        var dialog = CreateDialog(
            "发送前确认",
            new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "以下是本轮将发送给 Provider 的数据、历史和工具声明。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    previewBox,
                },
            },
            "确认并发送",
            "取消");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var userText = _prompt.Text.Trim();
        _prompt.Text = string.Empty;
        if (_messagePanel.Children.Count == 1
            && _messagePanel.Children[0] is Border { Tag: "empty" })
        {
            _messagePanel.Children.Clear();
        }

        AddMessage("user", userText);
        var streamed = new StreamingTextBatcher();
        var output = AddMessage("assistant", "正在生成…");
        output.Text = string.Empty;
        var finalTextApplied = false;
        var streamFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        void FlushStreamedText()
        {
            var chunk = streamed.Drain();
            if (chunk.Length == 0)
                return;
            output.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = chunk,
            });
            ScrollToBottom();
        }
        EventHandler<object>? flushHandler = (_, _) => FlushStreamedText();
        streamFlushTimer.Tick += flushHandler;
        streamFlushTimer.Start();
        _sendCancellation?.Dispose();
        _sendCancellation = new CancellationTokenSource();
        _isSending = true;
        _status.Text = "正在生成…";
        UpdateCommandState();
        var progress = new Progress<string>(delta =>
        {
            streamed.Append(delta);
        });
        try
        {
            var result = await _coordinator.SendAsync(
                _currentConversation,
                userText,
                progress,
                _sendCancellation.Token);
            streamFlushTimer.Stop();
            output.Text = result.Text;
            finalTextApplied = true;
            ShowProposals(result.ProposedChanges);
            if (result.ProposedChanges.Count > 0)
            {
                _workspaceSplit.IsPaneOpen = true;
            }
            _status.Text =
                $"完成 · 输入 {result.InputTokens} / 输出 {result.OutputTokens} Token";
            await ReloadConversationsAsync(_currentConversation.ConversationId);
        }
        catch
        {
            if (!finalTextApplied)
            {
                FlushStreamedText();
            }
            if (streamed.Length == 0)
            {
                output.Text = "本轮生成未完成。你可以修改消息后重试。";
            }

            throw;
        }
        finally
        {
            streamFlushTimer.Stop();
            streamFlushTimer.Tick -= flushHandler;
            if (!finalTextApplied)
            {
                FlushStreamedText();
            }
            _isSending = false;
            UpdateCommandState();
            ScrollToBottom();
        }
    }

    private async void OnApplyProposals(object sender, RoutedEventArgs args)
    {
        var selected = _proposalPanel.Children
            .OfType<CheckBox>()
            .Where(item => item.IsChecked == true)
            .Select(item => (PersonalAnimeChange)item.Tag)
            .ToList();
        if (selected.Count == 0)
        {
            _status.Text = "请先勾选要应用的提案。";
            return;
        }

        var summary = string.Join(
            Environment.NewLine + Environment.NewLine,
            selected.Select(item =>
                $"{item.Title}\n{DescribeChange(item)}\n理由：{item.Reason}"));
        var dialog = CreateDialog(
            "确认写回 AniMeido",
            new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = summary,
                    TextWrapping = TextWrapping.Wrap,
                },
                MaxHeight = 460,
            },
            "应用勾选项",
            "取消");
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var result = await _coordinator.ApplyChangesAsync(
                _currentConversation!,
                selected);
            _status.Text = string.Join(
                "；",
                result.Results.Select(item =>
                    $"{item.ChangeId[..Math.Min(8, item.ChangeId.Length)]}：{item.Message}"));
            var completed = result.Results
                .Where(item => item.Applied || item.WasAlreadyApplied)
                .Select(item => item.ChangeId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var checkBox in _proposalPanel.Children.OfType<CheckBox>())
            {
                if (checkBox.Tag is PersonalAnimeChange change
                    && completed.Contains(change.ChangeId))
                {
                    checkBox.IsEnabled = false;
                    checkBox.IsChecked = false;
                }
            }
        });
    }

    private async void OnExport(object sender, RoutedEventArgs args)
        => await RunUiAsync(async () =>
        {
            var path = Path.Combine(
                _paths.ExportDirectory,
                $"AniMeido-AI-conversations-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await _coordinator.ExportAsync(path);
            _status.Text = $"已导出：{path}";
        });

    private async void OnImport(object sender, RoutedEventArgs args)
    {
        var path = Directory.EnumerateFiles(
                _paths.ExportDirectory,
                "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null)
        {
            _status.Text = $"没有可导入文件：{_paths.ExportDirectory}";
            return;
        }

        await RunUiAsync(async () =>
        {
            await _coordinator.ImportAsync(path);
            await ReloadConversationsAsync();
            _status.Text = $"已导入：{path}";
        });
    }

    private async Task ReloadConversationsAsync(string? selectId = null)
    {
        var items = await _coordinator.GetConversationsAsync(_search.Text);
        var targetId = selectId
            ?? _currentConversation?.ConversationId
            ?? items.FirstOrDefault()?.ConversationId;
        ListViewItem? selectedView = null;
        AiConversation? selectedConversation = null;
        _suppressConversationSelection = true;
        try
        {
            _conversationList.Items.Clear();
            foreach (var item in items)
            {
                var view = new ListViewItem
                {
                    Content = BuildConversationListItem(item),
                    Tag = item,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                _conversationList.Items.Add(view);
                if (item.ConversationId == targetId)
                {
                    selectedView = view;
                    selectedConversation = item;
                }
            }

            _conversationList.SelectedItem = selectedView;
        }
        finally
        {
            _suppressConversationSelection = false;
        }

        if (selectedConversation is not null)
        {
            await LoadConversationAsync(selectedConversation);
        }
        else if (items.Count == 0)
        {
            _currentConversation = null;
            _currentSnapshot = null;
            ShowEmptyConversation();
            UpdateContextPresentation();
            UpdateCommandState();
        }
    }

    private async Task ReloadAnimeCatalogAsync()
    {
        var items = await _coordinator.QueryAnimeAsync(null);
        _animeCatalog.Clear();
        _animeCatalog.AddRange(items);
        _catalogCount.Text = $"AniMeido 中有 {items.Count} 个可选条目";
    }

    private FrameworkElement BuildConversationListItem(AiConversation item)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Padding = new Thickness(4, 7, 4, 7),
        };
        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{GetTaskTitle(item.TaskKind)} · {item.UpdatedAt.LocalDateTime:g}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.58,
            FontSize = 12,
        });
        return panel;
    }

    private static FrameworkElement BuildAnimeSelectionContent(
        PersonalAnimeSelectionItem item)
    {
        var details = new List<string>();
        if (item.TrackingStatus is not null)
        {
            details.Add(item.TrackingStatus.ToString()!);
        }

        if (item.HasPlan)
        {
            details.Add("有计划");
        }

        if (item.PersonalRating is not null)
        {
            details.Add($"个人 {item.PersonalRating:0.0}");
        }

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (details.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", details),
                FontSize = 12,
                Opacity = 0.58,
            });
        }

        return panel;
    }

    private TextBlock AddMessage(string role, string body)
    {
        var isUser = role == "user";
        var block = new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = isUser ? "你" : "AniMeido AI",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Opacity = 0.72,
        });
        content.Children.Add(block);
        var bubble = new Border
        {
            Child = content,
            Padding = new Thickness(14, 11, 14, 12),
            CornerRadius = new CornerRadius(12),
            Background = Application.Current.Resources[
                isUser
                    ? "AccentFillColorSecondaryBrush"
                    : "CardBackgroundFillColorDefaultBrush"] as Brush,
            HorizontalAlignment = isUser
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left,
            MaxWidth = 760,
        };
        _messagePanel.Children.Add(bubble);
        ScrollToBottom();
        return block;
    }

    private void ShowEmptyConversation(
        string title = "选择一项 AI 任务",
        string description = "每项任务有明确的数据范围和可执行边界；进入任务后仍可连续追问。",
        bool showTaskChoices = true)
    {
        _messagePanel.Children.Clear();
        var content = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        content.Children.Add(new FontIcon
        {
            Glyph = "\uE945",
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.65,
            MaxWidth = 460,
        });
        if (showTaskChoices)
        {
            var definitions = _coordinator.TaskDefinitions
                .OrderBy(item => item.Kind)
                .ToList();
            var choices = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(0, 18, 0, 0),
            };
            for (var index = 0; index < definitions.Count; index += 2)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                row.Children.Add(BuildTaskChoice(definitions[index]));
                if (index + 1 < definitions.Count)
                {
                    row.Children.Add(BuildTaskChoice(definitions[index + 1]));
                }

                choices.Children.Add(row);
            }

            content.Children.Add(choices);
        }
        _messagePanel.Children.Add(new Border
        {
            Tag = "empty",
            Child = content,
            Padding = new Thickness(24),
            Margin = new Thickness(0, showTaskChoices ? 56 : 120, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _conversationTitle.Text = _currentConversation is null
            ? "选择一项 AI 任务"
            : GetTaskTitle(_currentConversation.TaskKind);
        _conversationMeta.Text = _currentConversation is null
            ? "任务记录与追问历史只保存在 AI 插件本地"
            : _conversationMeta.Text;
        _status.Text = _currentConversation is null ? string.Empty : _status.Text;
    }

    private Button BuildTaskChoice(AiTaskDefinition definition)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock
        {
            Text = definition.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
        });
        text.Children.Add(new TextBlock
        {
            Text = definition.Description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            MaxWidth = 270,
        });
        text.Children.Add(new TextBlock
        {
            Text = SelectionRequirement(definition) + " · 支持任务内连续追问",
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        });
        var button = new Button
        {
            Content = text,
            Tag = definition,
            Width = 310,
            MinHeight = 82,
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        button.Click += OnCreateTask;
        return button;
    }

    private void UpdateContextPresentation()
    {
        _contextItems.Children.Clear();
        if (_currentSnapshot is null || _currentConversation is null)
        {
            _snapshotSummary.Text = "尚未配置任务数据。";
            _contextItems.Children.Add(new TextBlock
            {
                Text = "点击中间顶部的“编辑上下文”选择本轮允许发送的数据。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.62,
            });
            return;
        }

        _snapshotSummary.Text =
            $"快照 {_currentConversation.SnapshotRevision} · {_currentSnapshot.Items.Count} 部番剧\n{_currentSnapshot.CreatedAt.LocalDateTime:g}";
        foreach (var item in _currentSnapshot.Items.Take(8))
        {
            _contextItems.Children.Add(new TextBlock
            {
                Text = "• " + item.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (_currentSnapshot.Items.Count > 8)
        {
            _contextItems.Children.Add(new TextBlock
            {
                Text = $"另有 {_currentSnapshot.Items.Count - 8} 项",
                Opacity = 0.6,
            });
        }
    }

    private void ShowProposals(IReadOnlyList<PersonalAnimeChange> proposals)
    {
        _proposalPanel.Children.Clear();
        if (proposals.Count == 0)
        {
            _proposalPanel.Children.Add(new TextBlock
            {
                Text = "当前任务没有待审查的变更。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.62,
            });
            _applyButton.IsEnabled = false;
            _inspectorButton.Content = "任务详情";
            return;
        }

        foreach (var proposal in proposals)
        {
            _proposalPanel.Children.Add(new CheckBox
            {
                Content = $"{proposal.Title}\n{DescribeChange(proposal)}\n{proposal.Reason}",
                Tag = proposal,
                IsChecked = false,
            });
        }

        _applyButton.IsEnabled = true;
        _inspectorButton.Content = $"提案 ({proposals.Count})";
    }

    private string DescribeChange(PersonalAnimeChange change)
    {
        var current = _currentSnapshot?.Items.FirstOrDefault(
            item => item.AnimeId == change.AnimeId);
        return change.Kind switch
        {
            PersonalAnimeChangeKind.SetTrackingStatus =>
                $"追番状态：{current?.TrackingStatus?.ToString() ?? "未标记"} → {change.TrackingStatus}",
            PersonalAnimeChangeKind.UpsertPlan =>
                $"计划：优先级 {current?.Plan?.Priority.ToString() ?? "无"} / 日期 {current?.Plan?.TargetStartDate?.ToString() ?? "无"} → 优先级 {change.PlanPriority} / 日期 {change.PlanTargetStartDate}",
            PersonalAnimeChangeKind.ReplaceArchiveSummary =>
                $"档案概要：{Shorten(current?.ArchiveSummary)} → {Shorten(change.Text)}",
            PersonalAnimeChangeKind.AppendArchiveEntry =>
                $"观看感想：已有 {current?.ArchiveEntries.Count ?? 0} 条 → 追加 {Shorten(change.Text)}",
            _ => change.Kind.ToString(),
        };
    }

    private ContentDialog CreateDialog(
        string title,
        object content,
        string primaryText,
        string closeText)
        => new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };

    private string GetTaskTitle(AiTaskKind kind)
        => _coordinator.TaskDefinitions
            .First(item => item.Kind == kind)
            .Title;

    private static string SelectionRequirement(AiTaskDefinition definition)
        => definition.MinimumAnimeCount == 0
            ? $"可选择 0–{definition.MaximumAnimeCount} 项"
            : $"需要选择 {definition.MinimumAnimeCount}–{definition.MaximumAnimeCount} 项";

    private void UpdateCommandState()
    {
        _prompt.IsEnabled = _currentConversation is not null && !_isSending;
        _sendButton.IsEnabled = _currentConversation is not null
            && _currentSnapshot is not null
            && !_isSending;
        _cancelButton.IsEnabled = _isSending;
        _editContextButton.IsEnabled = _currentConversation is not null
            && !_isSending;
        _inspectorButton.IsEnabled = _currentConversation is not null;
    }

    private void ScrollToBottom()
        => DispatcherQueue.TryEnqueue(() => _messageScroll.ChangeView(
            null,
            _messageScroll.ScrollableHeight,
            null,
            disableAnimation: false));

    private static string Shorten(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "空"
            : value.Length <= 80 ? value : value[..80] + "…";

    private async Task RunUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "操作已取消。";
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or IOException
                or HttpRequestException
                or TimeoutException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or System.Text.Json.JsonException
                or Microsoft.Data.Sqlite.SqliteException
                or AiProviderException)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            UpdateCommandState();
        }
    }
}
