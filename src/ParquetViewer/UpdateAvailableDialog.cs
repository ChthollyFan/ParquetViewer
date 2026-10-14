using ParquetViewer.Controls;
using ParquetViewer.Helpers;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace ParquetViewer
{
    /// <summary>
    /// 启动时发现新版本后弹出的提示窗口：「更新」跳转到该版本的 GitHub 发布页面，
    /// 「取消」只关闭窗口；勾选「记住我的选择」再取消，调用方会记住该版本而不再提示。
    /// </summary>
    public partial class UpdateAvailableDialog : FormBase
    {
        //「记住我的选择」用小一号字体显示，该字体对象需要随窗口一起释放
        private readonly Font _rememberChoiceFont;

        /// <summary>
        /// 用户是否勾选了「记住我的选择」
        /// </summary>
        /// <remarks>
        /// 该选项只在用户点击「取消」时被调用方采纳：记住后同一个版本不再弹窗，
        /// 直到远端出现更新的版本（相当于每次更新之后该记忆自动失效）。
        /// </remarks>
        public bool RememberChoice => this.rememberChoiceCheckBox.Checked;

        /**
         * @brief 构造「发现新版本」提示窗口
         * @param latestVersion 远端最新正式版本号
         * @param currentVersion 当前程序版本号
         */
        public UpdateAvailableDialog(SemanticVersion latestVersion, SemanticVersion currentVersion)
        {
            InitializeComponent();

            this.Text = Resources.Strings.UpdateAvailableDialogTitle;
            this.messageLabel.Text = Resources.Strings.UpdateAvailableDialogMessageFormat
                .Format(latestVersion.ToString(), currentVersion.ToString());
            this.updateButton.Text = Resources.Strings.UpdateAvailableDialogUpdateButtonText;
            this.cancelButton.Text = Resources.Strings.UpdateAvailableDialogCancelButtonText;
            this.rememberChoiceCheckBox.Text = Resources.Strings.UpdateAvailableDialogRememberChoiceText;

            //小字提示比按钮正文小一号，避免抢走两个按钮的视觉重心
            this._rememberChoiceFont = new Font(this.Font.FontFamily, this.Font.Size - 1F);
            this.rememberChoiceCheckBox.Font = this._rememberChoiceFont;
            //复选框宽度随本地化文案变化，赋值完 Text 后再与「取消」按钮右对齐
            this.rememberChoiceCheckBox.Left = this.cancelButton.Right - this.rememberChoiceCheckBox.Width;
        }

        private void updateButton_Click(object sender, EventArgs e)
        {
            //真正的下载安装交给浏览器完成，这里只把用户的选择告诉调用方
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        public override void SetTheme(Theme theme)
        {
            base.SetTheme(theme);

            //FormBase 统一了窗口背景色，按钮沿用系统浅色样式才能保证文案可读
            this.updateButton.ForeColor = Color.Black;
            this.cancelButton.ForeColor = Color.Black;
        }
    }
}