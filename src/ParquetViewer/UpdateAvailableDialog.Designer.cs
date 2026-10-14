namespace ParquetViewer
{
    partial class UpdateAvailableDialog
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();

                //「记住我的选择」使用的小号字体由本窗口创建，需要一并释放
                this._rememberChoiceFont?.Dispose();
            }

            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.messageLabel = new System.Windows.Forms.Label();
            this.updateButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.rememberChoiceCheckBox = new System.Windows.Forms.CheckBox();
            this.SuspendLayout();
            //
            // messageLabel
            //
            this.messageLabel.AutoSize = false;
            this.messageLabel.Location = new System.Drawing.Point(18, 18);
            this.messageLabel.Name = "messageLabel";
            this.messageLabel.Size = new System.Drawing.Size(384, 70);
            this.messageLabel.TabIndex = 0;
            //
            // updateButton
            //
            this.updateButton.Location = new System.Drawing.Point(230, 98);
            this.updateButton.Name = "updateButton";
            this.updateButton.Size = new System.Drawing.Size(82, 30);
            this.updateButton.TabIndex = 1;
            this.updateButton.UseVisualStyleBackColor = true;
            this.updateButton.Click += new System.EventHandler(this.updateButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.Location = new System.Drawing.Point(320, 98);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(82, 30);
            this.cancelButton.TabIndex = 2;
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // rememberChoiceCheckBox
            //
            this.rememberChoiceCheckBox.AutoSize = true;
            this.rememberChoiceCheckBox.Location = new System.Drawing.Point(250, 136);
            this.rememberChoiceCheckBox.Name = "rememberChoiceCheckBox";
            this.rememberChoiceCheckBox.TabIndex = 3;
            this.rememberChoiceCheckBox.UseVisualStyleBackColor = true;
            //
            // UpdateAvailableDialog
            //
            this.AcceptButton = this.updateButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(420, 170);
            this.Controls.Add(this.rememberChoiceCheckBox);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.updateButton);
            this.Controls.Add(this.messageLabel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "UpdateAvailableDialog";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label messageLabel;
        private System.Windows.Forms.Button updateButton;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.CheckBox rememberChoiceCheckBox;
    }
}