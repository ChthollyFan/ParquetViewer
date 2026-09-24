using ParquetViewer.Controls;
using ParquetViewer.Engine;
using ParquetViewer.Engine.Exceptions;
using ParquetViewer.Helpers;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ParquetViewer
{
    public partial class MainForm : FormBase
    {
        private const int DefaultOffset = 0;
        private const int DefaultRowCountValue = 1000;

        /// <summary>
        /// 界面上的起始行号从 1 起算，内部偏移量仍按 0 起算传给引擎，只在界面层做换算
        /// </summary>
        private const int FirstRowNumber = 1;

        /// <summary>
        /// 单次加载行数的硬上限。
        /// 千万行级别的文件一次性载入需要上百 GB 内存，会直接让进程 OOM 崩溃，
        /// 因此无论从哪个入口（加载全部行按钮、记录数量输入框、总是加载全部记录设置）都不允许越过这个值。
        /// </summary>
        private const int MaxRowsPerLoad = 1_000_000;

        private readonly string DefaultFormTitle;

        #region Members
        private readonly string? fileToLoadOnLaunch = null;
        private string? _openFileOrFolderPath;
        private string? OpenFileOrFolderPath
        {
            get => this._openFileOrFolderPath;
            set
            {
                this._openFileOrFolderPath = value;
                this._openParquetEngine?.Dispose();
                this._openParquetEngine = null;
                this.SelectedFields = null;
                this.changeFieldsMenuStripButton.Enabled = false;
                this.getSQLCreateTableScriptToolStripMenuItem.Enabled = false;
                this.saveAsToolStripMenuItem.Enabled = false;
                this.metadataViewerToolStripMenuItem.Enabled = false;
                this.recordCountStatusBarLabel.Text = "0";
                this.totalRowCountStatusBarLabel.Text = "0";
                this.actualShownRecordCountLabel.Text = "0";
                this.mainGridView.DisposeAudioCells();
                this.MainDataSource?.Dispose();
                this.MainDataSource = null;
                this.loadAllRowsButton.Enabled = false;
                this.nextOffsetButton.Enabled = false;
                this.previousOffsetButton.Enabled = false;
                this.searchFilterTextBox.PlaceholderText = "WHERE ";
                // 切换文件后起始行复位到第 1 行
                this.offsetTextBox.SetTextQuiet(FirstRowNumber.ToString());
                this.currentOffset = DefaultOffset;
                this.mainGridView.ClearQuickPeekForms();
                this.mainGridView.ClearColumnFormatOverrides();
                this.ResetGetSQLCreateTableScriptToolStripMenuItemToolTipText();

                if (string.IsNullOrWhiteSpace(this._openFileOrFolderPath))
                {
                    this.Text = this.DefaultFormTitle;
                }
                else
                {
                    if (File.Exists(this._openFileOrFolderPath))
                        this.Text = string.Format(Resources.Strings.MainWindowOpenFileTitleFormat, this._openFileOrFolderPath);
                    else
                        this.Text = string.Format(Resources.Strings.MainWindowOpenFolderTitleFormat, this._openFileOrFolderPath);

                    this.changeFieldsMenuStripButton.Enabled = true;
                    this.saveAsToolStripMenuItem.Enabled = true;
                    this.getSQLCreateTableScriptToolStripMenuItem.Enabled = true;
                    this.metadataViewerToolStripMenuItem.Enabled = true;
                }
            }
        }

        private List<string>? selectedFields = null;
        private List<string>? SelectedFields
        {
            get => this.selectedFields;
            set
            {
                this.selectedFields = value?.ToList();

                //Check for duplicate fields (We don't support case sensitive field names unfortunately)
                var duplicateFields = this.selectedFields?.GroupBy(f => f.ToUpperInvariant()).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
                if (duplicateFields?.Count > 0)
                {
                    this.selectedFields = this.selectedFields!.Where(f => !duplicateFields.Any(df => df.Equals(f, StringComparison.InvariantCultureIgnoreCase))).ToList();

                    MessageBox.Show($"The following duplicate fields could not be loaded: {string.Join(',', duplicateFields)}. " +
                            $"{Environment.NewLine}{Environment.NewLine}Case sensitive field names are not currently supported.",
                            "Duplicate fields detected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                if (value?.Count > 0)
                {
                    LoadFileToGridview();
                }
            }
        }

        private int currentOffset = DefaultOffset;
        private int CurrentOffset
        {
            get => this.currentOffset;
            set
            {
                this.currentOffset = value;
                LoadFileToGridview();
            }
        }

        /// <summary>
        /// 界面上的起始行号，从 1 起算（即当前偏移量加 1）
        /// </summary>
        private int CurrentStartRow => this.CurrentOffset + FirstRowNumber;

        /// <summary>
        /// 把界面输入的起始行号钳制到当前文件的合法范围内
        /// </summary>
        /// <param name="startRow">界面输入的起始行号（1 起算）</param>
        /// <returns>已打开文件时不超过文件总行数，且始终不小于第 1 行</returns>
        private int ClampStartRow(int startRow)
        {
            if (startRow < FirstRowNumber)
            {
                return FirstRowNumber;
            }

            // 尚未打开文件时没有上限可依据，只保证不小于第 1 行
            if (this._openParquetEngine is null)
            {
                return startRow;
            }

            long recordCount = this._openParquetEngine.RecordCount;

            // 空文件没有任何可停在的行，统一回到第 1 行
            if (recordCount < FirstRowNumber)
            {
                return FirstRowNumber;
            }

            // 总行数超过 int 上限时 startRow 本来就无法越过它
            return startRow <= recordCount ? startRow : (int)Math.Min(recordCount, int.MaxValue);
        }

        private static int DefaultRowCount => DefaultRowCountValue;

        private int currentMaxRowCount = DefaultRowCount;
        private int CurrentMaxRowCount
        {
            get => this.currentMaxRowCount;
            set
            {
                // 统一在属性层做硬性截断：输入框、加载全部行按钮、总是加载全部记录设置都可能传进超大值
                this.currentMaxRowCount = Math.Min(value, MaxRowsPerLoad);
                LoadFileToGridview();
            }
        }

        private bool IsAnyFileOpen
            => !string.IsNullOrWhiteSpace(this.OpenFileOrFolderPath)
                && this._openParquetEngine is not null;

        private DataTable? mainDataSource;
        private DataTable? MainDataSource
        {
            get => this.mainDataSource;
            set
            {
                this.mainDataSource = value;
                this.mainGridView.DataSource = this.mainDataSource;

                if (this.mainDataSource is not null)
                {
                    this.loadAllRowsButton.Enabled = this.mainDataSource.Rows.Count < (this._openParquetEngine?.RecordCount ?? default);

                    // 当前偏移量再往前一页仍在文件范围内时，才还存在“下一个偏移量”可跳
                    this.nextOffsetButton.Enabled = this._openParquetEngine is not null
                        && (long)this.CurrentOffset + this.CurrentMaxRowCount < this._openParquetEngine.RecordCount;

                    // 已经不在第 1 行时才存在“上一个偏移量”可回到
                    this.previousOffsetButton.Enabled = this._openParquetEngine is not null
                        && this.CurrentOffset > 0;

                    SetSampleQueryAsPlaceHolder();
                }
            }
        }

        private IParquetEngine? _openParquetEngine = null;

        private (DateTime LastWriteTimeUtc, long Length)? _originalModifiedInfo;
        #endregion

        public MainForm()
        {
            this.ForeColor = System.Drawing.Color.Red;
            InitializeComponent();
            this.DefaultFormTitle = this.Text;
            this.offsetTextBox.SetTextQuiet(FirstRowNumber.ToString());
            this.recordCountTextBox.SetTextQuiet(DefaultRowCount.ToString());
            this.MainDataSource = new DataTable();
            this.OpenFileOrFolderPath = null;

            //Have to set these here because it gets deleted from the .Designer.cs file for some reason
            this.metadataViewerToolStripMenuItem.Image = Resources.Icons.text_file_icon_16x16.ToBitmap();
            this.iSO8601ToolStripMenuItem.ToolTipText = ExtensionMethods.ISO8601DateTimeFormat;
        }

        public MainForm(string? fileToOpenPath) : this()
        {
            if (fileToOpenPath is not null)
            {
                //The code below will be executed after the default constructor => this()
                this.fileToLoadOnLaunch = fileToOpenPath;
            }
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            //Open existing file on first load. Usually this means user double-clicked a parquet file with this utility as the default program.
            if (!string.IsNullOrWhiteSpace(this.fileToLoadOnLaunch))
            {
                await this.OpenNewFileOrFolder(this.fileToLoadOnLaunch);
            }

            //Check necessary toolstrip menu items
            this.RefreshDateFormatMenuItemSelection();
            this.alwaysLoadAllRecordsToolStripMenuItem.Checked = AppSettings.AlwaysLoadAllRecords;
            this.darkModeToolStripMenuItem.Checked = AppSettings.DarkMode;
            this.SetLanguageCheckmark();

            //Ask the user if they want to enable dark mode (only if their system is in dark mode)
            Program.AskUserIfTheyWantToSwitchToDarkMode();
        }

        private async Task<List<string>?> OpenFieldSelectionDialog(bool forceOpenDialog)
        {
            if (string.IsNullOrWhiteSpace(this.OpenFileOrFolderPath))
            {
                return null;
            }

            if (this._openParquetEngine == null)
            {
                try
                {
                    this._openParquetEngine = await Engine.ParquetNET.ParquetEngine.OpenFileOrFolderAsync(this.OpenFileOrFolderPath, default);
                }
                catch (Exception ex)
                {
                    if (this._openParquetEngine == null)
                    {
                        //cancel the file open
                        this.OpenFileOrFolderPath = null;
                    }

                    if (ex is AllFilesSkippedException afse)
                    {
                        HandleAllFilesSkippedException(afse);
                    }
                    else if (ex is SomeFilesSkippedException sfse)
                    {
                        HandleSomeFilesSkippedException(sfse);
                    }
                    else if (ex is Engine.Exceptions.FileReadException fre)
                    {
                        MainForm.HandleFileReadException(fre);
                    }
                    else if (ex is MultipleSchemasFoundException msfe)
                    {
                        HandleMultipleSchemasFoundException(msfe);
                    }
                    else if (ex is FileNotFoundException fnfe)
                    {
                        HandleFileNotFoundException(fnfe);
                    }
                    else if (ex is not OperationCanceledException)
                    {
                        throw;
                    }

                    return null;
                }
            }

            List<string>? fields = null;
            try
            {
                fields = this._openParquetEngine.Fields;
            }
            catch (ArgumentException ex) when (ex.Message.StartsWith("at least one field is required"))
            { /*swallow: This exception is thrown from Parquet.Net when the schema has no fields*/ }
            catch (Exception ex)
            {
                throw new Parquet.ParquetException(Resources.Errors.ParquetSchemaReadErrorMessage, ex);
            }

            if (fields?.Count > 0)
            {
                if (AppSettings.AlwaysSelectAllFields && !forceOpenDialog)
                {
                    return fields;
                }
                else
                {
                    using var fieldSelectionForm = new FieldsToLoadForm(fields, this.MainDataSource?.GetColumnNames() ?? Array.Empty<string>());
                    if (fieldSelectionForm.ShowDialog(this) == DialogResult.OK && fieldSelectionForm.NewSelectedFields?.Count > 0)
                    {
                        return fieldSelectionForm.NewSelectedFields;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            else
            {
                ShowError(Resources.Errors.NoFieldsFoundErrorMessage, Resources.Errors.NoFieldsFoundErrorTitle);
                return null;
            }
        }

        private async void LoadFileToGridview()
        {
            if (this._openParquetEngine is null)
                return;

#if RELEASE_SELFCONTAINED
            //Self contained release has both Parquet.NET and DuckDB engines included as the file size remains the same.
            try
            {
                await this.LoadFileToGridviewImpl(this._openParquetEngine);
            }
            catch (Exception unhandledEx)
            {
                //Try DuckDB if Parquet.NET fails
                if (this._openParquetEngine is Engine.DuckDB.ParquetEngine)
                    throw;

                try
                {
                    var duckDbEngine = await Engine.DuckDB.ParquetEngine.OpenFileOrFolderAsync(this.OpenFileOrFolderPath!, default);
                    await LoadFileToGridviewImpl(duckDbEngine);
                    SwapEngines(duckDbEngine);
                }
                catch (Exception duckDbEx)
                {
                    //If DuckDB fails too, bail
                    throw new Exceptions.RowsReadException(unhandledEx, duckDbEx);
                }
            }

            void SwapEngines(IParquetEngine newEngine)
            {
                this._openParquetEngine.DisposeSafely();
                this._openParquetEngine = newEngine;
            }
#else
            await this.LoadFileToGridviewImpl(this._openParquetEngine);
#endif

            this._originalModifiedInfo = null;
        }

        private async Task LoadFileToGridviewImpl(IParquetEngine engine)
        {
            var stopwatch = Stopwatch.StartNew(); var loadTime = TimeSpan.Zero; var indexTime = TimeSpan.Zero;
            LoadingIcon? loadingIcon = null;
            try
            {
                if (!this.IsAnyFileOpen)
                    return;

                if (this.SelectedFields is null || this.SelectedFields.Count == 0)
                    return;

                if (!File.Exists(this.OpenFileOrFolderPath) && !Directory.Exists(this.OpenFileOrFolderPath))
                {
                    ShowError(Resources.Errors.OpenFileNoLongerExistsErrorMessageFormat.Format(this.OpenFileOrFolderPath + Environment.NewLine));
                    return;
                }

                long cellCount = this.SelectedFields.Count * Math.Min(this.CurrentMaxRowCount, engine.RecordCount - this.CurrentOffset);
                loadingIcon = this.ShowLoadingIcon(Resources.Strings.LoadingDataLabelText, cellCount);

                var intermediateResult = await Task.Run(async () =>
                {
                    return await engine.ReadRowsAsync(this.SelectedFields, this.CurrentOffset, this.CurrentMaxRowCount, loadingIcon.CancellationToken, loadingIcon);
                }, loadingIcon.CancellationToken);

                loadTime = stopwatch.Elapsed;
                bool showIndexingProgress = false;
                if (loadTime > TimeSpan.FromSeconds(4))
                {
                    //Don't bother showing the indexing step if the data load was really fast because we know 
                    //indexing will be instantaneous. It looks better this way in my opinion.
                    loadingIcon.Reset(Resources.Strings.IndexingDataLabelText);
                    showIndexingProgress = true;
                }

                var finalResult = await Task.Run(() => intermediateResult.Invoke(showIndexingProgress), loadingIcon.CancellationToken);
                indexTime = stopwatch.Elapsed - loadTime;

                // 状态栏按 1 起算显示本次加载覆盖的行号范围（含首尾两行）
                this.recordCountStatusBarLabel.Text = string.Format(
                    Resources.Strings.LoadedRecordCountRangeFormat,
                    this.CurrentStartRow,
                    this.CurrentOffset + finalResult.Rows.Count);
                this.totalRowCountStatusBarLabel.Text = engine.RecordCount.ToString();
                this.actualShownRecordCountLabel.Text = finalResult.Rows.Count.ToString();

                this.MainDataSource = finalResult;

                //重新加载数据会用新的 DataTable 替换旧表导致过滤条件丢失，此处自动重新应用搜索框中的查询
                TryApplyFilterAutomatically();
            }
            catch (AllFilesSkippedException ex)
            {
                HandleAllFilesSkippedException(ex);
            }
            catch (SomeFilesSkippedException ex)
            {
                HandleSomeFilesSkippedException(ex);
            }
            catch (FileReadException ex)
            {
                HandleFileReadException(ex);
            }
            catch (MultipleSchemasFoundException ex)
            {
                HandleMultipleSchemasFoundException(ex);
            }
            catch (MalformedFieldException ex)
            {
                HandleMalformedFieldException(ex);
            }
            catch (DecimalOverflowException ex)
            {
                HandleDecimalOverflowException(ex);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    throw;
            }
            finally
            {
                stopwatch.Stop();

                TimeSpan totalTime = stopwatch.Elapsed;
                TimeSpan renderTime = totalTime - loadTime - indexTime;

                //Little secret performance counter
                this.showingStatusBarLabel.ToolTipText = $"Total time: {totalTime:mm\\:ss\\.ff}" + Environment.NewLine +
                $"    Load time: {loadTime:mm\\:ss\\.ff}" + Environment.NewLine +
                $"    Index time: {indexTime:mm\\:ss\\.ff}" + Environment.NewLine +
                $"    Render time: {renderTime:mm\\:ss\\.ff}" + Environment.NewLine +
                $"Engine: {(engine is Engine.ParquetNET.ParquetEngine ? "ParquetNET" : "DuckDB")}";

                loadingIcon?.Dispose();
            }
        }

        /// <summary>
        /// 数据重新加载后自动重新应用搜索框中的查询条件，避免范围变化后用户需要手动再次点击执行
        /// </summary>
        /// <remarks>查询仅对当前加载到内存中的行生效；自动应用失败时静默恢复为无过滤，不打断加载流程</remarks>
        private void TryApplyFilterAutomatically()
        {
            if (this.MainDataSource is null)
                return;

            string queryText = GetFilterQueryFromTextBox();
            if (string.IsNullOrWhiteSpace(queryText))
                return;

            try
            {
                this.Cursor = Cursors.WaitCursor;
                this.MainDataSource.DefaultView.RowFilter = queryText;
            }
            catch
            {
                //自动应用失败时静默恢复为无过滤，不打断加载流程；用户手动执行时仍可看到具体的错误提示
                this.MainDataSource.DefaultView.RowFilter = null;
            }
            finally
            {
                this.Cursor = Cursors.Default;
                this.actualShownRecordCountLabel.Text = this.MainDataSource.DefaultView.Count.ToString();
            }
        }

        private async Task OpenNewFileOrFolder(string fileOrFolderPath)
        {
            this.OpenFileOrFolderPath = fileOrFolderPath;

            var fieldList = await this.OpenFieldSelectionDialog(false);
            var wasOpenSuccess = this._openParquetEngine is not null;

            if (wasOpenSuccess && AppSettings.AlwaysLoadAllRecords)
            {
                long totalRecordCount = this._openParquetEngine!.RecordCount;

                // 勾选了“总是加载全部记录”的用户每次打开大文件都会走这条分支，
                // 同样要先警示并截断到上限，否则照样会把程序卡死
                int rowsToLoad = (int)Math.Min(totalRecordCount, MaxRowsPerLoad);
                if (totalRecordCount > MaxRowsPerLoad && !this.ConfirmLoadingLargeFile(totalRecordCount, rowsToLoad))
                {
                    rowsToLoad = DefaultRowCount;
                }

                this.currentMaxRowCount = rowsToLoad;
                this.recordCountTextBox.SetTextQuiet(rowsToLoad.ToString());
            }
            else
            {
                this.currentMaxRowCount = DefaultRowCount;
                this.recordCountTextBox.SetTextQuiet(DefaultRowCount.ToString());
            }

            if (fieldList is not null)
            {
                this.SelectedFields = fieldList; //triggers a file load
                AppSettings.OpenedFileCount++;
                Program.AskUserForFileExtensionAssociation();
            }
        }

        /// <summary>
        /// Checks <see cref="AppSettings.DateTimeDisplayFormat"/> and checks/unchecks 
        /// the appropriate date format options located in the menu bar.
        /// </summary>
        private void RefreshDateFormatMenuItemSelection()
        {
            this.defaultToolStripMenuItem.Checked = false;
            this.iSO8601ToolStripMenuItem.Checked = false;
            this.customDateFormatToolStripMenuItem.Checked = false;

            switch (AppSettings.DateTimeDisplayFormat)
            {
                case DateFormat.Default:
                    this.defaultToolStripMenuItem.Checked = true;
                    break;
                case DateFormat.ISO8601:
                    this.iSO8601ToolStripMenuItem.Checked = true;
                    break;
                case DateFormat.Custom:
                    this.customDateFormatToolStripMenuItem.Checked = true;
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Provides the user with a sample query in <see cref="searchFilterTextBox"/> 
        /// using the first primitive field available in the dataset. If none are found,
        /// the placeholder won't contain a sample. Only the "WHERE ".
        /// </summary>
        private void SetSampleQueryAsPlaceHolder()
        {
            this.searchFilterTextBox.PlaceholderText = "WHERE ";

            if (this.MainDataSource is null || this.MainDataSource.Rows.Count == 0)
                return;

            var simpleColumn = this.MainDataSource.Columns.AsEnumerable().FirstOrDefault(c => c.DataType.IsSimple());
            if (simpleColumn is null)
                return;

            //find a value we can use as a sample
            object sampleSimpleValue = DBNull.Value; int counter = 1000;
            foreach (DataRow row in this.MainDataSource.Rows)
            {
                sampleSimpleValue = row[simpleColumn];
                if (counter <= 0 || (sampleSimpleValue != DBNull.Value))
                {
                    break;
                }
                counter--;
            }

            if (sampleSimpleValue == DBNull.Value)
                return;

            string placeholder = ParquetGridView.GenerateFilterQuery(simpleColumn.ColumnName, simpleColumn.DataType, sampleSimpleValue);
            if (placeholder.Length < 100) //Only set the placeholder query if it's reasonably short
                this.searchFilterTextBox.PlaceholderText = $"WHERE {placeholder}";
        }


        private void SetLanguageCheckmark()
        {
            if (AppSettings.UserSelectedCulture is not null)
            {
                this.languageToolStripMenuItem.DropDownItems.OfType<ToolStripMenuItem>().ToList().ForEach(languageToolStripItem =>
                {
                    languageToolStripItem.Checked = languageToolStripItem.Tag?.ToString() == AppSettings.UserSelectedCulture.ToString();
                });
            }
            else
            {
                //We default to English
                this.englishToolStripMenuItem.Checked = true;
            }
        }
    }
}