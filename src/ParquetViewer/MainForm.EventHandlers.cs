using ParquetViewer.Engine;
using ParquetViewer.Engine.Types;
using ParquetViewer.Exceptions;
using ParquetViewer.Helpers;
using System;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ParquetViewer
{
    public partial class MainForm
    {
        [GeneratedRegex("^WHERE ")]
        private static partial Regex QueryUselessPartRegex();


        private void offsetTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
            }
        }

        private void recordsToTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
            }
        }

        private void offsetTextBox_TextChanged(object sender, EventArgs? e)
        {
            var textbox = (TextBox)sender;
            if (int.TryParse(textbox.Text, out var offset))
                this.CurrentOffset = offset;
            else
                textbox.Text = this.CurrentOffset.ToString();
        }

        private void recordsToTextBox_TextChanged(object sender, EventArgs? e)
        {
            var textbox = (TextBox)sender;
            if (int.TryParse(textbox.Text, out var recordCount) && recordCount > 0)
            {
                this.CurrentMaxRowCount = recordCount;

                // 输入值被单次加载上限截断时把文本框回写成实际生效的数字，
                // 否则用户看到的行数和真正加载的行数会不一致
                if (this.CurrentMaxRowCount != recordCount)
                {
                    textbox.Text = this.CurrentMaxRowCount.ToString();
                }
            }
            else
                textbox.Text = this.CurrentMaxRowCount.ToString();
        }

        private void searchFilterTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == Convert.ToChar(Keys.Return))
            {
                this.runQueryButton_Click(this.runQueryButton, null);
            }
            else if (e.KeyChar == Convert.ToChar(Keys.Escape))
            {
                this.clearFilterButton_Click(this.clearFilterButton, null);
            }
        }

        private async void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                var files = e.Data?.GetData(DataFormats.FileDrop) as string[];
                if (files?.Length > 0)
                {
                    await this.OpenNewFileOrFolder(files[0]);
                }
            }
            catch
            {
                this.OpenFileOrFolderPath = null;
                throw;
            }
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        }

        private void searchFilterLabel_Click(object sender, EventArgs e)
        {
            MessageBox.Show(Resources.Strings.QuerySyntaxHelpText, Resources.Strings.QuerySyntaxHelpTitle);
        }

        private void mainGridView_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            this.actualShownRecordCountLabel.Text = this.mainGridView.RowCount.ToString();
        }

        private void showingStatusBarLabel_Click(object sender, EventArgs e)
        {
            //This is just here in case I want to add debug info
        }

        private void searchFilterTextBox_Enter(object sender, EventArgs e)
        {
            if (sender is TextBox searchBox)
            {
                if (string.IsNullOrWhiteSpace(searchBox.Text))
                {
                    searchBox.Text = "WHERE ";
                }
            }
        }

        private void searchFilterTextBox_Leave(object sender, EventArgs e)
        {
            if (sender is TextBox searchBox)
            {
                if (searchBox.Text.Trim().Equals("WHERE", StringComparison.OrdinalIgnoreCase))
                {
                    searchBox.Text = string.Empty; //show the placeholder
                }
            }
        }

        private void loadAllRowsButton_EnabledChanged(object sender, EventArgs e)
        {
            if (sender is Button loadAllRecordsButton)
            {
                loadAllRecordsButton.FlatAppearance.MouseOverBackColor = Color.Transparent;
                loadAllRecordsButton.FlatAppearance.MouseDownBackColor = Color.Transparent;

                if (loadAllRecordsButton.Enabled)
                {
                    loadAllRecordsButton.Image = Resources.Icons.next_blue;
                }
                else
                {
                    loadAllRecordsButton.Image = Resources.Icons.next_disabled;
                }
            }
        }

        private void loadAllRowsButton_Click(object? sender, EventArgs? e)
        {
            if (this._openParquetEngine is null)
            {
                return;
            }

            long totalRecordCount = this._openParquetEngine.RecordCount;
            int rowsToLoad = (int)Math.Min(totalRecordCount, MaxRowsPerLoad);

            // 超过单次加载上限时先警示：千万行级别的文件一次载入会耗尽内存并让进程崩溃
            if (totalRecordCount > MaxRowsPerLoad
                && !this.ConfirmLoadingLargeFile(totalRecordCount, rowsToLoad))
            {
                return;
            }

            //Force file reload to happen instantly by triggering the event handler ourselves
            this.recordCountTextBox.SetTextQuiet(rowsToLoad.ToString());
            this.recordsToTextBox_TextChanged(this.recordCountTextBox, null);
        }

        /// <summary>
        /// 询问用户是否接受“只加载前 N 行”。文件超过单次加载上限时使用。
        /// </summary>
        /// <param name="totalRecordCount">文件总行数</param>
        /// <param name="rowsToLoad">准备加载的行数（已按上限截断）</param>
        /// <returns>用户选择继续加载时返回 true</returns>
        private bool ConfirmLoadingLargeFile(long totalRecordCount, int rowsToLoad)
        {
            string message = string.Format(
                Resources.Strings.LargeFileLoadWarningMessageFormat,
                totalRecordCount.ToString("N0"),
                rowsToLoad.ToString("N0"));

            DialogResult result = MessageBox.Show(
                this,
                message,
                Resources.Strings.LargeFileLoadWarningTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            return result == DialogResult.Yes;
        }

        private void nextOffsetButton_Click(object? sender, EventArgs? e)
        {
            if (this._openParquetEngine is null)
            {
                return;
            }

            // 按当前页大小向后翻一页；先转成 long 再相加，避免偏移量和记录数都接近 int 上限时溢出
            long nextOffset = (long)this.CurrentOffset + this.CurrentMaxRowCount;

            // 已经到末尾或偏移量超出 int 可表示范围时什么都不做（正常情况下按钮此时已禁用）
            if (nextOffset >= this._openParquetEngine.RecordCount || nextOffset > int.MaxValue)
            {
                return;
            }

            //Force file reload to happen instantly by triggering the event handler ourselves
            this.offsetTextBox.SetTextQuiet(((int)nextOffset).ToString());
            this.offsetTextBox_TextChanged(this.offsetTextBox, null);
        }

        private void nextOffsetButton_EnabledChanged(object sender, EventArgs e)
        {
            if (sender is Button nextOffsetRecordsButton)
            {
                nextOffsetRecordsButton.FlatAppearance.MouseOverBackColor = Color.Transparent;
                nextOffsetRecordsButton.FlatAppearance.MouseDownBackColor = Color.Transparent;

                nextOffsetRecordsButton.Image = nextOffsetRecordsButton.Enabled
                    ? Resources.Icons.next_blue
                    : Resources.Icons.next_disabled;
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.E && this.loadAllRowsButton.Enabled)
            {
                this.loadAllRowsButton_Click(null, null);
            }
            else if (e.Control && e.KeyCode == Keys.R && this._openParquetEngine is not null) //Reload shortcut
            {
                LoadFileToGridview();
            }
        }

        /// <summary>
        /// 从搜索框读取并规范化查询条件：去掉 WHERE 前缀，并将 list/map/struct/bytearray 等复杂列转换为字符串比较
        /// </summary>
        /// <returns>规范化后的查询条件；搜索框无有效内容时返回空字符串</returns>
        private string GetFilterQueryFromTextBox()
        {
            string queryText = this.searchFilterTextBox.Text ?? string.Empty;
            queryText = QueryUselessPartRegex().Replace(queryText, string.Empty).Trim();

            //Treat list, map, and struct types as strings by casting them automatically
            foreach (var complexField in this.mainGridView.Columns.OfType<DataGridViewColumn>()
                .Where(c => c.ValueType.ImplementsInterface<IListValue>() || c.ValueType.ImplementsInterface<IMapValue>()
                    || c.ValueType.ImplementsInterface<IStructValue>() || c.ValueType.ImplementsInterface<IByteArrayValue>())
                .Select(c => c.Name))
            {
                //This isn't perfect but it should handle most cases
                queryText = queryText.Replace(complexField, $"CONVERT({complexField}, System.String)", StringComparison.InvariantCultureIgnoreCase);
            }

            return queryText;
        }

        private void runQueryButton_Click(object sender, EventArgs? e)
        {
            try
            {
                if (!this.IsAnyFileOpen || this.MainDataSource is null)
                    return;

                string queryText = GetFilterQueryFromTextBox();

                if (string.IsNullOrWhiteSpace(queryText)
                    || this.MainDataSource.DefaultView.RowFilter == queryText) //No need to execute the same query again
                {
                    return;
                }

                try
                {
                    this.Cursor = Cursors.WaitCursor;
                    this.MainDataSource.DefaultView.RowFilter = queryText;
                }
                catch (Exception ex)
                {
                    this.MainDataSource.DefaultView.RowFilter = null;
                    throw new InvalidQueryException(ex);
                }
                finally
                {
                    this.Cursor = Cursors.Default;
                    this.actualShownRecordCountLabel.Text = this.MainDataSource.DefaultView.Count.ToString();
                }
            }
            catch (InvalidQueryException ex)
            {
                MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + ex.InnerException?.Message,
                    Resources.Errors.InvalidQueryErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
                throw;
            }
        }

        private void clearFilterButton_Click(object sender, EventArgs? e)
        {
            if (!string.IsNullOrEmpty(this.MainDataSource?.DefaultView.RowFilter))
            {
                try
                {
                    this.Cursor = Cursors.WaitCursor;
                    this.MainDataSource.DefaultView.RowFilter = null;
                }
                finally
                {
                    this.Cursor = Cursors.Default;
                    this.actualShownRecordCountLabel.Text = this.MainDataSource.DefaultView.Count.ToString();

                }
            }
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                //Hide context menu on minimize to avoid a glitch where
                //the context menu won't go away until you click on it.
                this.mainGridView.CloseContextMenu();
            }
        }

        private void languageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (sender is not ToolStripItem toolStripItem)
            {
                return;
            }

            var targetCulture = toolStripItem.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(targetCulture))
            {
                targetCulture = "en-US"; //our default culture
            }

            if (!UtilityMethods.TryParseCultureInfo(targetCulture, out CultureInfo? newCultureInfo))
            {
                return; //invalid culture
            }

            if (newCultureInfo.Equals(CultureInfo.CurrentUICulture))
            {
                return; //no change
            }

            if (MessageBox.Show(this,
                Resources.Strings.LanguageChangeConfirmationMessage,
                Resources.Strings.LanguageChangeConfirmationTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return; //user cancelled
            }

            AppSettings.UserSelectedCulture = newCultureInfo;
            UtilityMethods.RestartApplication();
        }

        /// <remarks>Originally I implemented a FileSystemWatcher but it seems network drives are not reliable with that.
        /// Not sure how common that is but this implementation without it is simpler and I'm hoping not too IO intensive</remarks>
        private async void fileIntegrityCheckingTimer_Tick(object sender, EventArgs e)
        {
            if (this.OpenFileOrFolderPath is null || this._openParquetEngine is null)
                return; //no file open

            this.fileIntegrityCheckingTimer.Stop();
            try
            {
                var fileDeletedSuffix = $" ({Resources.Strings.OpenFileNoLongerExistsTitleSuffix})";
                var fileModifiedSuffix = $" ({Resources.Strings.OpenFileWasModifiedTitleSuffix})";

                if (this._originalModifiedInfo is null)
                {
                    ResetTitle();
                }

                var alreadyHasDeletedSuffix = this.Text.EndsWith(fileDeletedSuffix);

                //Perform file system checks in a background thread avoid blocking the UI thread.
                //Only really relevant when opening a folder with many files on a network drive.
                var engineSnapshot = this._openParquetEngine;
                var lastModifiedInfo = await Task.Run(() => TryGetLastModifiedInfo(engineSnapshot, this.OpenFileOrFolderPath));
                if (!ReferenceEquals(engineSnapshot, this._openParquetEngine))
                    return; //the user has opened a different file/folder while we were checking the file system, so ignore this result

                if (lastModifiedInfo is null && !alreadyHasDeletedSuffix)
                {
                    ResetTitle();
                    //File or folder no longer exists. In this case let's not mark this timer as handled
                    //and let it keep running in case the file/folder is restored later.
                    this.Text += fileDeletedSuffix;
                    return;
                }
                else if (lastModifiedInfo is not null && alreadyHasDeletedSuffix)
                {
                    ResetTitle();
                }

                if (lastModifiedInfo is not null)
                {
                    if (_originalModifiedInfo is null)
                    {
                        _originalModifiedInfo = lastModifiedInfo;
                    }
                    else if (_originalModifiedInfo != lastModifiedInfo && !this.Text.EndsWith(fileModifiedSuffix))
                    {
                        ResetTitle();
                        this.Text += fileModifiedSuffix;
                    }
                }

                void ResetTitle()
                {
                    if (this.Text.EndsWith(fileModifiedSuffix))
                        this.Text = this.Text.Replace(fileModifiedSuffix, string.Empty);
                    else if (this.Text.EndsWith(fileDeletedSuffix))
                        this.Text = this.Text.Replace(fileDeletedSuffix, string.Empty);
                }
            }
            catch (Exception)
            {
                //忽略完整性检查中的一切异常（文件被删除、被占用等），避免反复打扰用户
            }
            finally
            {
                this.fileIntegrityCheckingTimer.Start();
            }

            //Returns the last modified date and size of the open file, or the most recent last modified
            //date and total combined size of all open files in the folder.
            static (DateTime LastModifiedUtc, long Length)? TryGetLastModifiedInfo(IParquetEngine engine, string openFileOrFolderPath)
            {
                if (engine is null)
                {
                    return null; //no open file;
                }

                DateTime latest = Directory.Exists(openFileOrFolderPath) ? Directory.GetCreationTimeUtc(openFileOrFolderPath) : DateTime.MinValue;
                long totalLength = 0;
                bool foundAny = false;
                var counter = 0;

                foreach (var filePath in engine.GetOpenParquetFilePaths())
                {
                    if (counter >= 250)
                    {
                        //We don't want to check too many files in case the user has a folder with a lot of files open.
                        //This is a safeguard against performance issues.
                        break;
                    }

                    var info = new FileInfo(filePath);
                    if (!info.Exists)
                    {
                        return null; //file was deleted
                    }

                    //There's a chance the file could be deleted between the time we check for existence above and when we access .Length and .LastWriteTimeUtc below.
                    //This is fine as the caller has a try-catch block that catches IOException types.
                    totalLength += info.Length;

                    if (!foundAny || info.LastWriteTimeUtc > latest)
                    {
                        latest = info.LastWriteTimeUtc;
                        foundAny = true;
                    }

                    counter++;
                }

                return (latest, totalLength);
            }
        }
    }
}