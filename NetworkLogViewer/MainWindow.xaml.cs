﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kamilla;
using Kamilla.Network;
using Kamilla.Network.Logging;
using Kamilla.Network.Parsing;
using Kamilla.Network.Protocols;
using Kamilla.Network.Viewing;
using Kamilla.WPF;
using Microsoft.Win32;
using NetworkLogViewer.ViewTabs;

namespace NetworkLogViewer
{
    partial class MainWindow : Window
    {
        ViewerImplementation m_implementation;
        internal ViewerImplementation Implementation { get { return m_implementation; } }

        public static string OpenFileName
        {
            get { return Configuration.GetValue("Open File Name", string.Empty); }
            set { Configuration.SetValue("Open File Name", value); }
        }

        public static string SaveFileName
        {
            get { return Configuration.GetValue("Save File Name", string.Empty); }
            set { Configuration.SetValue("Save File Name", value); }
        }

        #region .ctor
        public MainWindow()
        {
            AppDomain.CurrentDomain.UnhandledException += (o, e) =>
            {
                Console.WriteLine("Error: " + e.ExceptionObject.ToString());
                MessageWindow.Show(this, Strings.Error, e.ExceptionObject.ToString());
            };

            UICulture.Initialize();
            UICulture.UICultureChanged += new EventHandler(UICulture_UICultureChanged);

            ConsoleWriter.Initialize();

            m_implementation = new ViewerImplementation(this);

            m_implementation.ProtocolChanged += new ProtocolChangedEventHandler(MainWindow_ProtocolChanged);
            m_implementation.NetworkLogChanged += new NetworkLogChangedEventHandler(MainWindow_NetworkLogChanged);

            InitializeComponent();

            App.InitializeConsole(this);

            // Perform operations that alter UI here
            {
                // Use Minimized as special value
                var stateNotSet = WindowState.Minimized;
                var state = Configuration.GetValue("Window State", stateNotSet);

                if (state != WindowState.Maximized)
                {
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var height = Configuration.GetValue("Window Height", this.Height);
                    var width = Configuration.GetValue("Window Width", this.Width);

                    if (width / screenWidth > 0.8)
                        width = screenWidth * 0.8;

                    if (height / screenHeight > 0.8)
                        height = screenHeight * 0.8;

                    this.Width = width;
                    this.Height = height;

                    var left = Math.Max(Configuration.GetValue("Window Left", this.Left), 0.0);
                    var top = Math.Max(Configuration.GetValue("Window Top", this.Top), 0.0);

                    if (left != 0.0 && top != 0.0)
                    {
                        if (left + width > screenWidth)
                            left = screenWidth - width;

                        if (top + height > screenHeight)
                            top = screenHeight - top;

                        this.Left = left;
                        this.Top = top;
                    }
                }

                if (state != stateNotSet)
                    this.WindowState = state;

                int val = Configuration.GetValue("Number of Views", 2);
                this.SetNViews(val);

                var result = Configuration.GetValue("Vertical Splitter", (double[])null);
                if (result != null && result.Length == 2)
                {
                    this.VerticalGrid.RowDefinitions[1].Height = new GridLength(result[0], GridUnitType.Star);
                    this.VerticalGrid.RowDefinitions[2].Height = new GridLength(result[1], GridUnitType.Star);
                }

                InitializeSkins();
            }

            // Command Bindings
            this.CommandBindings.AddRange(new[] {
                new CommandBinding(ApplicationCommands.Close, ApplicationClose_Executed),
                new CommandBinding(ApplicationCommands.Open, ApplicationOpen_Executed),
                new CommandBinding(NetworkLogViewerCommands.OpenConsole, OpenConsole_Executed),
                new CommandBinding(NetworkLogViewerCommands.CloseFile, CloseFile_Executed, (o, e) =>
                {
                    e.CanExecute = this.CurrentLog != null;
                    e.Handled = true;
                }),
                new CommandBinding(NetworkLogViewerCommands.GoToPacketN, GoToPacketN_Executed, CanSearch),
                new CommandBinding(NetworkLogViewerCommands.NextError, NextError_Executed, CanSearch),
                new CommandBinding(NetworkLogViewerCommands.NextUndefinedParser, NextUndefinedParser_Executed, CanSearch),
                new CommandBinding(NetworkLogViewerCommands.NextUnknownOpcode, NextUnknownOpcode_Executed, (o, e) =>
                {
                    CanSearch(o, e);

                    if (e.CanExecute)
                        e.CanExecute = this.CurrentProtocol != null && this.CurrentProtocol.OpcodesEnumType != null;
                }),
                new CommandBinding(NetworkLogViewerCommands.Search, Search_Executed, CanSearch),
                new CommandBinding(NetworkLogViewerCommands.SearchUp, SearchUp_Executed, CanSearch),
                new CommandBinding(NetworkLogViewerCommands.SearchDown, SearchDown_Executed, CanSearch),
            });

            // Key Bindings
            this.InputBindings.AddRange(new[] {
                new KeyBinding(ApplicationCommands.Close, Key.X, ModifierKeys.Alt),
                //new KeyBinding(ApplicationCommands.Open, (KeyGesture)ApplicationCommands.Open.InputGestures[0]),
                new KeyBinding(NetworkLogViewerCommands.OpenConsole, Key.F12, ModifierKeys.None),
            });

            // Background Workers
            this.ui_loadingWorker = new BackgroundWorker()
            {
                WorkerSupportsCancellation = true
            };
            this.ui_loadingWorker.DoWork += new DoWorkEventHandler(this.ui_loadingWorker_DoWork);
            this.ui_loadingWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(this.ui_loadingWorker_RunWorkerCompleted);

            this.ui_readingWorker = new BackgroundWorker()
            {
                WorkerSupportsCancellation = true
            };
            this.ui_readingWorker.DoWork += new DoWorkEventHandler(this.ui_readingWorker_DoWork);
            this.ui_readingWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(this.ui_readingWorker_RunWorkerCompleted);

            ui_savingWorker = new BackgroundWorker()
            {
                WorkerSupportsCancellation = true,
                WorkerReportsProgress = true
            };
            ui_savingWorker.DoWork += new DoWorkEventHandler(ui_savingWorker_DoWork);
            ui_savingWorker.ProgressChanged += new ProgressChangedEventHandler(ui_savingWorker_ProgressChanged);
            ui_savingWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(ui_savingWorker_RunWorkerCompleted);

            ui_searchWorker = new BackgroundWorker()
            {
                WorkerSupportsCancellation = true,
                WorkerReportsProgress = true
            };
            ui_searchWorker.DoWork += new DoWorkEventHandler(ui_searchWorker_DoWork);
            ui_searchWorker.ProgressChanged += new ProgressChangedEventHandler(ui_searchWorker_ProgressChanged);
            ui_searchWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(ui_searchWorker_RunWorkerCompleted);

            ui_lvPackets.ItemsSource = m_implementation.m_items;

            Console.WriteLine("MainWindow initialized.");
        }
        #endregion

        #region Implementation Interop
        protected override void OnStyleChanged(Style oldStyle, Style newStyle)
        {
            base.OnStyleChanged(oldStyle, newStyle);

            m_implementation.OnStyleChanged(oldStyle, newStyle);
        }

        internal Protocol CurrentProtocol
        {
            get { return m_implementation.CurrentProtocol; }
            set { m_implementation.SetProtocol(value); }
        }

        internal NetworkLog CurrentLog
        {
            get { return m_implementation.CurrentLog; }
            set { m_implementation.SetLog(value); }
        }
        #endregion

        #region Languages
        static CultureInfo[] s_supportedCultures = new[]
        {
            CultureInfo.GetCultureInfo("en"),
            CultureInfo.GetCultureInfo("ru"),
        };

        void InitializeLanguages()
        {
            this.ThreadSafe(_ =>
            {
                foreach (var culture in s_supportedCultures)
                {
                    var item = new MenuItem();

                    item.Header = culture.EnglishName;
                    item.Tag = culture;
                    item.Click += new RoutedEventHandler(LanguageItem_Click);

                    ui_miLanguage.Items.Add(item);
                }

                SetLanguageItemForCulture(UICulture.Culture);
            });
        }

        void LanguageItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (MenuItem)sender;
            var culture = (CultureInfo)item.Tag;

            UICulture.Culture = culture;
            // Items modified in event handler
        }

        void UICulture_UICultureChanged(object sender, EventArgs e)
        {
            SetLanguageItemForCulture(UICulture.Culture);
        }

        void SetLanguageItemForCulture(CultureInfo culture)
        {
            var code = culture.Name.Substring(0, 2);
            foreach (MenuItem item in ui_miLanguage.Items)
                item.IsChecked = ((CultureInfo)item.Tag).Name.SubstringEquals(0, code);
        }
        #endregion

        #region Loading Window
        Stack<LoadingState> m_loadingStateStack = new Stack<LoadingState>();
        LoadingWindow m_loadingWindow;

        void LoadingStatePush(LoadingState state)
        {
            this.ThreadSafeBegin(safeThis =>
            {
                m_loadingStateStack.Push(state);

                if (m_loadingWindow == null)
                    m_loadingWindow = new LoadingWindow(this);

                m_loadingWindow.SetLoadingState(state);

                m_loadingWindow.Owner = safeThis;
                if (!m_loadingWindow.IsVisible)
                    m_loadingWindow.ShowDialog();
            });
        }

        void LoadingStateSetProgress(int percent)
        {
            this.ThreadSafeBegin(_ => _.m_loadingWindow.SetProgress(percent));
        }

        void LoadingStatePop()
        {
            this.ThreadSafe(safeThis =>
            {
                if (m_loadingStateStack.Count == 0)
                    throw new InvalidOperationException("Loading State stack is empty.");

                m_loadingStateStack.Pop();

                if (m_loadingStateStack.Count != 0)
                    m_loadingWindow.SetLoadingState(m_loadingStateStack.Peek());
                else
                    m_loadingWindow.Hide();
            });
        }
        #endregion

        #region Commands
        void ApplicationClose_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            this.Close();

            e.Handled = true;
        }

        OpenFileDialog m_openFileDialog;
        void ApplicationOpen_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (m_openFileDialog == null)
            {
                m_openFileDialog = new OpenFileDialog();
                m_openFileDialog.AddExtension = false;
                m_openFileDialog.Filter = NetworkLogFactory.AllFileFiltersWithAny;
                m_openFileDialog.FilterIndex = NetworkLogFactory.AllFileFiltersWithAnyCount;
                m_openFileDialog.CheckFileExists = true;
                try
                {
                    var file = OpenFileName;
                    m_openFileDialog.FileName = Path.GetFileName(file);
                    m_openFileDialog.InitialDirectory = Path.GetDirectoryName(file);
                }
                catch
                {
                }
                m_openFileDialog.Multiselect = false;
            }

            var result = m_openFileDialog.ShowDialog(this);
            if (true == result)
            {
                var filename = m_openFileDialog.FileName;
                OpenFileName = filename;
                OpenFile(filename);
            }

            e.Handled = true;
        }

        void OpenConsole_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            var console = App.ConsoleWindow;

            if (!console.IsVisible)
                console.Show();

            if (!console.IsFocused)
                console.Focus();

            e.Handled = true;
        }

        void CloseFile_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            this.CloseFile();

            e.Handled = true;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = false;
            ui_readingWorker.CancelAsync();
            ui_savingWorker.CancelAsync();
            m_implementation.CloseFile();

            using (Configuration.SuspendSaving())
            {
                m_implementation.SaveSettings();
                Configuration.SetValue("Number of Views", m_currentNViews);
                this.SaveCurrentViews();
                Configuration.SetValue("Vertical Splitter", new[] {
                    this.VerticalGrid.RowDefinitions[1].Height.Value,
                    this.VerticalGrid.RowDefinitions[2].Height.Value,
                });
                Configuration.SetValue("Window State", this.WindowState);
                Configuration.SetValue("Window Height", this.Height);
                Configuration.SetValue("Window Width", this.Width);
                Configuration.SetValue("Window Left", this.Left);
                Configuration.SetValue("Window Top", this.Top);
                this.CurrentLog = null;
                this.CurrentProtocol = null;
            }
        }

        void CloseFile()
        {
            m_implementation.CloseFile();
        }

        private void DropCache_Click(object sender, RoutedEventArgs e)
        {
            m_implementation.DropCache();
            this.UpdateViews();
        }

        private void AutoParse_CheckedChanged(object sender, RoutedEventArgs e)
        {
            m_implementation.AutoParse = ui_miAutoParse.IsChecked;
        }

        private void AutoDropCache_CheckedChanged(object sender, RoutedEventArgs e)
        {
            m_implementation.EnableDeallocQueue = ui_miAutoDropCache.IsChecked;
        }
        #endregion

        #region Reading
        BackgroundWorker ui_readingWorker;
        string m_currentFile;

        void OpenFile(string filename)
        {
            NetworkLog log;
            try
            {
                log = NetworkLogFactory.GetNetworkLog(filename);
            }
            catch
            {
                MessageWindow.Show(this, "FAIL!", "FAIL!");
                return;
            }
            if (log == null)
                throw new NotImplementedException("Select Network Log window is not implemented");

            this.OpenFile(filename, log);
        }

        void OpenFile(string filename, NetworkLog log)
        {
            if (filename == null)
                throw new ArgumentNullException("filename");

            if (log == null)
                throw new ArgumentNullException("log");

            this.CloseFile();

            m_currentFile = filename;
            this.CurrentLog = log;

            ui_readingWorker.RunWorkerAsync();
            this.LoadingStatePush(new LoadingState(string.Format(Strings.LoadingFile, filename)));
        }

        private void ui_readingWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            UICulture.Initialize();

            var sw = Stopwatch.StartNew();

            this.CurrentLog.OpenForReading(m_currentFile);

            if (this.CurrentLog.Capacity > m_implementation.m_items.Capacity)
                m_implementation.m_items.Capacity = this.CurrentLog.Capacity;

            m_implementation.m_items.SuspendUpdating();
            this.CurrentLog.Read(progress =>
            {
                LoadingStateSetProgress(progress);
            });

            e.Result = this.CurrentLog.SuggestedProtocol ?? ProtocolManager.FindWrapper(typeof(DefaultProtocol));

            sw.Stop();
            Console.WriteLine("Reading Worker finished in {0}", sw.Elapsed);
        }

        private void ui_readingWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.CurrentLog.CloseStream();

            var wrapper = e.Result as ProtocolWrapper;
            if (wrapper != null)
            {
                this.CurrentProtocol = wrapper.Activate();
            }

            var sw = Stopwatch.StartNew();
            m_implementation.m_items.ResumeUpdating();
            m_implementation.m_items.Update();
            sw.Stop();
            Console.WriteLine("Updated items in {0}", sw.Elapsed);
            LoadingStatePop();

            if (e.Error != null)
            {
                MessageWindow.Show(this, Strings.Error, Strings.ErrorReading.LocalizedFormat(e.Error.ToString()));
            }
        }

        void MainWindow_NetworkLogChanged(object sender, NetworkLogChangedEventArgs e)
        {
            var newLog = e.NewLog;
            ui_sbiNetworkLog.Content = newLog != null ? newLog.Name : Strings.NoNetworkLog;

            UpdateUIAsProtocolOrLogChanges();
        }
        #endregion

        #region Loading
        BackgroundWorker ui_loadingWorker;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ui_loadingWorker.RunWorkerAsync();
            this.LoadingStatePush(new LoadingState("Loading..."));
        }

        private void ui_loadingWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            UICulture.Initialize();

            TypeManager.Initialize();
            ProtocolManager.Initialize();
            NetworkLogFactory.Initialize();
            InitializeProtocols();
            InitializeLanguages();
            m_implementation.LoadSettings();
            this.ThreadSafeBegin(_ =>
            {
                _.ui_miAutoDropCache.IsChecked = _.m_implementation.EnableDeallocQueue;
                _.ui_miAutoParse.IsChecked = _.m_implementation.AutoParse;
            });
        }

        private void ui_loadingWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.LoadingStatePop();
        }
        #endregion

        #region Protocol
        void InitializeProtocols()
        {
            ui_miProtocol.ThreadSafeBegin(mi =>
            {
                mi.BeginInit();

                foreach (var wrapper in ProtocolManager.ProtocolWrappers)
                {
                    var item = new MenuItem();
                    item.Header = wrapper.Name;
                    item.Tag = wrapper;
                    item.Click += new RoutedEventHandler(protocolItem_Click);
                    if (this.CurrentProtocol == null)
                    {
                        this.CurrentProtocol = wrapper.Activate();
                        item.IsChecked = true;
                    }
                    mi.Items.Add(item);
                }

                mi.EndInit();
            });
        }

        void protocolItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (MenuItem)sender;
            var wrapper = (ProtocolWrapper)item.Tag;

            var protocol = wrapper.Activate();

            this.CurrentProtocol = protocol;
        }

        void MainWindow_ProtocolChanged(object sender, ProtocolChangedEventArgs e)
        {
            var newProtocol = e.NewProtocol;
            var newProtocolWrapper = (ProtocolWrapper)null;
            if (newProtocol != null)
                newProtocolWrapper = newProtocol.Wrapper;

            this.ThreadSafeBegin(_ =>
            {
                UpdateUIAsProtocolOrLogChanges();

                if (newProtocol != null)
                {
                    ui_sbiProtocol.Content = newProtocol.Name;
                    ui_lvPackets.View = newProtocol.View;
                }
                else
                {
                    ui_sbiProtocol.Content = Strings.NoProtocol;
                    ui_lvPackets.View = null;
                }

                foreach (MenuItem itrItem in ui_miProtocol.Items)
                    itrItem.IsChecked = newProtocolWrapper == (ProtocolWrapper)itrItem.Tag;

                this.UpdateViews();
            });
        }
        #endregion

        #region Skins
        Dictionary<string, Style> m_skins;

        void InitializeSkins()
        {
            m_skins = new Dictionary<string, Style>()
            {
                { "KamillaStyle", (Style)this.FindResource("KamillaStyle") },
                { "Windows", (Style)this.FindResource("DefaultStyle") },
            };

            var resources = Strings.ResourceManager;
            var culture = CultureInfo.CurrentUICulture;

            int i = 0;
            foreach (var skin in m_skins)
            {
                var item = new MenuItem();
                item.Header = resources.GetString("Skin_" + skin.Key, culture);
                item.Tag = skin.Key;
                item.Click += new RoutedEventHandler(skinItem_Click);

                ui_miSkins.Items.Add(item);

                ++i;
            }

            var defaultStyle = "KamillaStyle";
            var usedSkin = Configuration.GetValue("Skin", defaultStyle);
            try
            {
                SetSkin(usedSkin);
            }
            catch
            {
                SetSkin(defaultStyle);
            }
        }

        void skinItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (MenuItem)sender;
            var skinName = (string)item.Tag;

            SetSkin(skinName);
        }

        void SetSkin(string name)
        {
            Style style;
            if (!m_skins.TryGetValue(name, out style))
                throw new ArgumentException("Skin '" + name + "' not found.");

            if (style == this.Style)
                return;

            this.Style = style;
            Configuration.SetValue("Skin", name);

            foreach (MenuItem item in ui_miSkins.Items)
                item.IsChecked = name == (string)item.Tag;
        }
        #endregion

        #region Viewing
        public ViewerItem SelectedItem
        {
            get
            {
                int index = ui_lvPackets.SelectedIndex;
                if (index >= 0)
                    return m_implementation.m_items[index];

                return null;
            }
        }

        static Type[] s_viewTabTypes = new[]
        {
            typeof(PacketContents),
            typeof(ParsedText),
            typeof(BinaryContents),
            typeof(TextContents),
            typeof(ImageContents),
        };

        int m_currentNViews;
        GridSplitter[] m_splitters = new GridSplitter[0];
        TabControl[] m_currentViews = new TabControl[0];

        private void ui_miViewsCount_Click(object sender, EventArgs e)
        {
            var str = ((MenuItem)sender).Tag.ToString();
            int nViews = int.Parse(str);

            if (nViews != m_currentNViews)
                this.SetNViews(nViews);
        }

        void SetNViews(int nViews)
        {
            if (m_currentNViews != 0)
                this.SaveCurrentViews();

            m_currentNViews = nViews;

            //Win32.SuspendDrawing(this.WindowHandle);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            ViewsGrid.ColumnDefinitions.Clear();
            ViewsGrid.Children.Clear();

            m_splitters = new GridSplitter[nViews - 1];
            m_currentViews = new TabControl[nViews];

            var distances = Configuration.GetValue("View" + nViews + " Distances Pct",
                Enumerable.Repeat(1.0 / nViews, nViews).ToArray());

            Array.Resize(ref distances, nViews);

            for (int i = 0; i < nViews; ++i)
            {
                var column = new ColumnDefinition();
                column.Width = new GridLength(distances[i], GridUnitType.Star);
                column.MinWidth = 50.0;
                ViewsGrid.ColumnDefinitions.Add(column);
            }

            for (int i = 0; i < m_splitters.Length; ++i)
            {
                var splitter = new GridSplitter();
                Panel.SetZIndex(splitter, 100);
                splitter.Background = Brushes.Transparent;
                splitter.Width = 6;
                splitter.HorizontalAlignment = HorizontalAlignment.Left;
                splitter.VerticalAlignment = VerticalAlignment.Stretch;
                splitter.Margin = new Thickness(-3.0, 0.0, 0.0, 0.0);
                ViewsGrid.Children.Add(splitter);
                Grid.SetColumn(splitter, i + 1);
            }

            var selectedTabs = Configuration.GetValue("View" + nViews + " Selected Tabs",
                Enumerable.Range(0, nViews).ToArray());

            Array.Resize(ref selectedTabs, nViews);
            for (int i = 0; i < selectedTabs.Length; ++i)
            {
                if (selectedTabs[i] >= s_viewTabTypes.Length)
                    selectedTabs[i] %= s_viewTabTypes.Length;
            }

            for (int i = 0; i < nViews; ++i)
            {
                var tc = new TabControl();
                ViewsGrid.Children.Add(tc);
                Grid.SetColumn(tc, i);
                m_currentViews[i] = tc;

                // Initialize Tab Control

                foreach (var type in s_viewTabTypes)
                {
                    var content = (IViewTab)Activator.CreateInstance(type);

                    var tabItem = new TabItem();
                    tabItem.Content = content;
                    tabItem.Header = content.Header;
                    tc.Items.Add(tabItem);
                }

                tc.SelectedItem = tc.Items[selectedTabs[i]];
                tc.SelectionChanged += new SelectionChangedEventHandler(tc_Selected);
            }

            var nViewsStr = nViews.ToString();
            foreach (MenuItem item in ui_miViewsColumns.Items)
                item.IsChecked = item.Tag.ToString() == nViewsStr;

            //Win32.ResumeDrawing(this.WindowHandle);

            sw.Stop();
            Console.WriteLine("Debug: Spent {0} on building {1} views", sw.Elapsed, m_currentNViews);

            UpdateViews();
        }

        void tc_Selected(object sender, SelectionChangedEventArgs e)
        {
            int index = ui_lvPackets.SelectedIndex;

            if (index >= 0)
            {
                var tab = (IViewTab)((TabItem)((TabControl)sender).SelectedItem).Content;
                if (!tab.IsFilled)
                    tab.Fill(this.CurrentProtocol, m_implementation.m_items[index]);
            }

            e.Handled = true;
        }

        void UpdateViews()
        {
            var protocol = this.CurrentProtocol;
            int index = ui_lvPackets.SelectedIndex;
            ViewerItem item = null;
            if (index >= 0)
                item = m_implementation.m_items[index];

            foreach (var tc in m_currentViews)
            {
                foreach (TabItem tab in tc.Items)
                    ((IViewTab)tab.Content).Reset();

                if (index >= 0)
                    ((IViewTab)((TabItem)tc.SelectedItem).Content).Fill(protocol, item);
            }
        }

        void SaveCurrentViews()
        {
            var nViews = m_currentNViews;
            var distances = new double[nViews];
            var selectedTabs = new int[nViews];

            for (int i = 0; i < nViews; i++)
                distances[i] = this.ViewsGrid.ColumnDefinitions[i].Width.Value;

            for (int i = 0; i < nViews; i++)
                selectedTabs[i] = m_currentViews[i].SelectedIndex;

            Configuration.SetValue("View" + nViews + " Distances Pct", distances);
            Configuration.SetValue("View" + nViews + " Selected Tabs", selectedTabs);
        }

        private void ui_lvPackets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.UpdateViews();
            e.Handled = true;
        }

        private void AlignPanels_Click(object sender, RoutedEventArgs e)
        {
            double width = 1.0 / m_currentNViews;
            foreach (var column in ViewsGrid.ColumnDefinitions)
                column.Width = new GridLength(width, GridUnitType.Star);
        }
        #endregion

        #region Saving
        BackgroundWorker ui_savingWorker;
        private void ui_miSaveParserOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog();
            dialog.AddExtension = true;
            dialog.Filter = Strings.TextFiles + " (*.txt)|*.txt|" + NetworkStrings.AllFiles + " (*.*)|*.*";
            dialog.FilterIndex = 0;
            try
            {
                var file = MainWindow.SaveFileName;
                dialog.FileName = Path.GetFileName(file);
                dialog.InitialDirectory = Path.GetDirectoryName(file);
            }
            catch
            {
            }

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            var filename = dialog.FileName;

            StreamWriter writer;
            try
            {
                writer = new StreamWriter(filename, false, Encoding.UTF8);
            }
            catch
            {
                MessageWindow.Show(this, Strings.Error, Strings.FailedToOpenFile);
                return;
            }

            ui_savingWorker.RunWorkerAsync(writer);
            this.LoadingStatePush(new LoadingState(Strings.ParsingPackets, _ => _.ui_savingWorker.CancelAsync()));
        }

        void ui_savingWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = e.Argument;
            var worker = (BackgroundWorker)sender;
            var writer = (StreamWriter)e.Argument;

            int progress = 0;
            var items = m_implementation.m_items;
            int count = items.Count;
            var protocol = this.CurrentProtocol;
            var format = "__ " + Strings.PacketN + " _________________________";
            for (int i = 0; i < count; i++)
            {
                if (worker.CancellationPending)
                    return;

                var item = items[i];

                var parser = item.Parser;
                if (parser == null)
                {
                    protocol.CreateParser(item);
                    parser = item.Parser;
                }

                if (!parser.IsParsed)
                    parser.Parse();

                writer.WriteLine(format.LocalizedFormat(i));
                writer.WriteLine(protocol.PacketContentsViewHeader(item));

                var text = parser.ParsedText;
                if (!string.IsNullOrEmpty(text))
                    writer.WriteLine(text);

                int newProgress = i * 100 / count;
                if (newProgress != progress)
                {
                    progress = newProgress;
                    worker.ReportProgress(progress);
                }
            }
        }

        void ui_savingWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            LoadingStateSetProgress(e.ProgressPercentage);
        }

        void ui_savingWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.LoadingStatePop();

            ((StreamWriter)e.Result).Close();

            if (e.Error != null)
                MessageWindow.Show(this, Strings.Error, e.Error.ToString());
        }
        #endregion

        #region Search
        internal SearchMode m_searchMode;
        internal bool m_regex;
        internal bool m_matchCase;
        internal bool m_allowChars;
        SearchWindow m_searchWindow;

        BackgroundWorker ui_searchWorker;

        void OpenSearchWindow()
        {
            if (m_searchWindow == null)
                m_searchWindow = new SearchWindow(this);

            if (!m_searchWindow.IsVisible)
                m_searchWindow.Show();

            m_searchWindow.Focus();
        }

        void CanSearch(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = this.CurrentProtocol != null && this.CurrentLog != null;
            e.Handled = true;
        }

        void Search_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            this.OpenSearchWindow();
        }

        void GoToPacketN_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;

            var window = new GoToPacketWindow(this);
            var result = window.ShowDialog();
            if (result != true)
                return;

            this.FinishSearch(m_implementation.m_items[window.Index]);
        }

        void NextError_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;

            this.StartSearch(new SearchRequest(true, true, item =>
            {
                var parser = item.Parser;
                if (parser == null)
                {
                    item.Viewer.CurrentProtocol.CreateParser(item);
                    parser = item.Parser;
                }

                if (!parser.IsParsed)
                    parser.Parse();

                return parser.ParsingError;
            }, FinishSearch));
        }

        void NextUndefinedParser_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;

            this.StartSearch(new SearchRequest(true, true, item =>
            {
                var parser = item.Parser;
                if (parser == null)
                {
                    item.Viewer.CurrentProtocol.CreateParser(item);
                    parser = item.Parser;
                }

                return parser is UndefinedPacketParser;
            }, FinishSearch));
        }

        void NextUnknownOpcode_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;

            var values = (uint[])this.CurrentProtocol.OpcodesEnumType.GetEnumValues();

            this.StartSearch(new SearchRequest(true, true, item =>
            {
                var packet = item.Packet as IPacketWithOpcode;
                if (packet == null)
                    return false;

                return !values.Contains(packet.Opcode);
            }, FinishSearch));
        }

        void SearchUp_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (m_searchWindow == null)
            {
                this.OpenSearchWindow();
                return;
            }

            var matcher = this.GetSearchMatcher();
            if (matcher == null)
                return;

            this.StartSearch(new SearchRequest(false, true, matcher, FinishSearch));
        }

        void SearchDown_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (m_searchWindow == null)
            {
                this.OpenSearchWindow();
                return;
            }

            var matcher = this.GetSearchMatcher();
            if (matcher == null)
                return;

            this.StartSearch(new SearchRequest(true, true, matcher, FinishSearch));
        }

        bool ToByteSequence(string s, out byte?[] result)
        {
            var tokens = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int count = tokens.Length;

            result = new byte?[count];
            for (int i = 0; i < count; i++)
			{
                var token = tokens[i];
                if (token == "?" || token == "??")
                    result[i] = null;
                else
                {
                    byte b;
                    if (!byte.TryParse(token, NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out b))
                    {
                        MessageWindow.Show(this, Strings.Error,
                            Strings.SearchString2ByteSeqFailed.LocalizedFormat(s));
                        return false;
                    }
                    result[i] = b;
                }
			}

            return true;
        }

        internal Predicate<ViewerItem> GetSearchMatcher()
        {
            var text = m_searchWindow.ui_cbSearch.Text;
            byte?[] byteSequence;
            Regex regex;
            Predicate<string> textMatcher;

            switch (m_searchMode)
            {
                case SearchMode.Opcodes:
                    var type = this.CurrentProtocol.OpcodesEnumType;
                    if (type == null)
                    {
                        MessageWindow.Show(this, Strings.Error, Strings.SearchFailedNoOpcodes);
                        return null;
                    }
                    uint opcode;
                    try
                    {
                        try
                        {
                            opcode = (uint)Enum.Parse(type, text, true);
                        }
                        catch
                        {
                            opcode = text.ParseUInt32();
                        }
                    }
                    catch
                    {
                        MessageWindow.Show(this, Strings.Error,
                            Strings.SearchString2OpcodeFailed.LocalizedFormat(text));
                        return null;
                    }
                    return item =>
                    {
                        var packet = item.Packet as IPacketWithOpcode;
                        if (packet == null)
                            return false;

                        return packet.Opcode == opcode;
                    };
                case SearchMode.BinaryContents:
                    if (!ToByteSequence(text, out byteSequence))
                        return null;

                    return item =>
                    {
                        var parser = item.Parser;
                        if (parser == null)
                        {
                            this.CurrentProtocol.CreateParser(item);
                            parser = item.Parser;
                        }

                        if (!parser.IsParsed)
                            parser.Parse();

                        var datas = ParsingHelper.ExtractBinaryDatas(this.CurrentProtocol, item);
                        for (int i = 0; i < datas.Length; i++)
                        {
                            if (datas[i].Item2.IndexOfSequence(byteSequence) >= 0)
                                return true;
                        }

                        return false;
                    };
                case SearchMode.PacketContents:
                    if (!ToByteSequence(text, out byteSequence))
                        return null;

                    return item => item.Packet.Data.IndexOfSequence(byteSequence) >= 0;
                case SearchMode.ParserOutput:
                case SearchMode.TextContents:
                    if (m_regex)
                    {
                        try
                        {
                            regex = new Regex(text, m_matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                        }
                        catch
                        {
                            MessageWindow.Show(this, Strings.Error,
                                Strings.SearchString2RegexFailed.LocalizedFormat(text));
                            return null;
                        }

                        textMatcher = s => regex.IsMatch(s);
                    }
                    else
                    {
                        var searchPattern = text;
                        if (m_allowChars)
                        {
                            searchPattern = searchPattern
                                .Replace("\\r", "\r")
                                .Replace("\\n", "\n")
                                .Replace("\\t", "\t")
                                .Replace("\\0", "\0");
                        }
                        var comparison = m_matchCase ? StringComparison.InvariantCulture
                            : StringComparison.InvariantCultureIgnoreCase;

                        textMatcher = s => s.IndexOf(searchPattern, comparison) >= 0;
                    }

                    if (m_searchMode == SearchMode.TextContents)
                    {
                        return item =>
                        {
                            var parser = item.Parser;
                            if (parser == null)
                            {
                                this.CurrentProtocol.CreateParser(item);
                                parser = item.Parser;
                            }

                            if (!parser.IsParsed)
                                parser.Parse();

                            var datas = ParsingHelper.ExtractStrings(this.CurrentProtocol, item);
                            for (int i = 0; i < datas.Length; i++)
                            {
                                if (textMatcher(datas[i].Item2))
                                    return true;
                            }

                            return false;
                        };
                    }
                    else
                    {
                        return item =>
                        {
                            var parser = item.Parser;
                            if (parser == null)
                            {
                                this.CurrentProtocol.CreateParser(item);
                                parser = item.Parser;
                            }

                            if (!parser.IsParsed)
                                parser.Parse();

                            return textMatcher(parser.ParsedText ?? string.Empty);
                        };
                    }
            }

            return null;
        }

        internal void StartSearch(SearchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");

            ui_searchWorker.RunWorkerAsync(request);
            this.LoadingStatePush(new LoadingState(Strings.Searching, _ => _.ui_searchWorker.CancelAsync()));
        }

        void ui_searchWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = -1;
            var request = (SearchRequest)e.Argument;
            var worker = (BackgroundWorker)sender;

            int delta = request.IsDown ? 1 : -1;
            int start = request.IsContinue ? (ui_lvPackets.ThreadSafe(_ => _.SelectedIndex) + delta) : 0;

            int progress = 0;
            var items = m_implementation.m_items;
            int count = items.Count;
            ViewerItem result = null;

            for (int i = start; i < count && i >= 0; i += delta)
            {
                if (worker.CancellationPending)
                    return;

                var item = items[i];
                if (request.Matches(item))
                {
                    result = item;
                    break;
                }

                int newProgress = i * 100 / count;
                if (newProgress != progress)
                {
                    progress = newProgress;
                    worker.ReportProgress(progress);
                }
            }

            e.Result = new Tuple<SearchRequest, ViewerItem>(request, result);
        }

        void ui_searchWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            LoadingStateSetProgress(e.ProgressPercentage);
        }

        void ui_searchWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            LoadingStatePop();

            if (e.Error != null)
            {
                MessageWindow.Show(this, Strings.Error, e.Error.ToString());
                return;
            }

            var tuple = (Tuple<SearchRequest, ViewerItem>)e.Result;

            tuple.Item1.Completed(tuple.Item2);
        }

        void FinishSearch(ViewerItem item)
        {
            if (item != null)
            {
                ui_lvPackets.SelectedIndex = item.Index;
                ui_lvPackets.ScrollIntoView(item);
                ((ListViewItem)ui_lvPackets.ItemContainerGenerator.ContainerFromItem(item)).Focus();
            }
            else
                MessageWindow.Show(this, Strings.Menu_Search, Strings.Search_NotFound);
        }
        #endregion

        void UpdateUIAsProtocolOrLogChanges()
        {
            this.ThreadSafeBegin(_ =>
            {
                bool haveProtocolAndLog = _.CurrentProtocol != null && _.CurrentLog != null;

                ui_miSaveBinaryContents.IsEnabled = haveProtocolAndLog;
                ui_miSaveParserOutput.IsEnabled = haveProtocolAndLog;
                ui_miSaveTextContents.IsEnabled = haveProtocolAndLog;
            });
        }
    }
}
