namespace EVEMon.ApiCredentialsManagement
{
    partial class ManualTokenInputForm
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
            if (disposing && (components != null))
            {
                components.Dispose();
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
            this.instructionsLabel = new System.Windows.Forms.Label();
            this.copyUrlButton = new System.Windows.Forms.Button();
            this.openBrowserButton = new System.Windows.Forms.Button();
            this.tokenUrlLabel = new System.Windows.Forms.Label();
            this.tokenUrlTextBox = new System.Windows.Forms.TextBox();
            this.okButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // instructionsLabel
            // 
            this.instructionsLabel.AutoSize = true;
            this.instructionsLabel.Location = new System.Drawing.Point(12, 12);
            this.instructionsLabel.Name = "instructionsLabel";
            this.instructionsLabel.Size = new System.Drawing.Size(100, 12);
            this.instructionsLabel.TabIndex = 0;
            this.instructionsLabel.Text = "Instructions";
            // 
            // copyUrlButton
            // 
            this.copyUrlButton.Location = new System.Drawing.Point(12, 120);
            this.copyUrlButton.Name = "copyUrlButton";
            this.copyUrlButton.Size = new System.Drawing.Size(150, 28);
            this.copyUrlButton.TabIndex = 1;
            this.copyUrlButton.Text = "复制授权链接";
            this.copyUrlButton.UseVisualStyleBackColor = true;
            this.copyUrlButton.Click += new System.EventHandler(this.copyUrlButton_Click);
            // 
            // openBrowserButton
            // 
            this.openBrowserButton.Location = new System.Drawing.Point(168, 120);
            this.openBrowserButton.Name = "openBrowserButton";
            this.openBrowserButton.Size = new System.Drawing.Size(150, 28);
            this.openBrowserButton.TabIndex = 2;
            this.openBrowserButton.Text = "打开授权页面";
            this.openBrowserButton.UseVisualStyleBackColor = true;
            this.openBrowserButton.Click += new System.EventHandler(this.openBrowserButton_Click);
            // 
            // tokenUrlLabel
            // 
            this.tokenUrlLabel.AutoSize = true;
            this.tokenUrlLabel.Location = new System.Drawing.Point(12, 165);
            this.tokenUrlLabel.Name = "tokenUrlLabel";
            this.tokenUrlLabel.Size = new System.Drawing.Size(100, 12);
            this.tokenUrlLabel.TabIndex = 3;
            this.tokenUrlLabel.Text = "粘贴回调 URL：";
            // 
            // tokenUrlTextBox
            // 
            this.tokenUrlTextBox.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tokenUrlTextBox.Font = new System.Drawing.Font("Consolas", 8F);
            this.tokenUrlTextBox.Location = new System.Drawing.Point(12, 185);
            this.tokenUrlTextBox.Multiline = true;
            this.tokenUrlTextBox.Name = "tokenUrlTextBox";
            this.tokenUrlTextBox.Size = new System.Drawing.Size(410, 80);
            this.tokenUrlTextBox.TabIndex = 4;
            // 
            // okButton
            // 
            this.okButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.okButton.Location = new System.Drawing.Point(228, 278);
            this.okButton.Name = "okButton";
            this.okButton.Size = new System.Drawing.Size(90, 28);
            this.okButton.TabIndex = 5;
            this.okButton.Text = "确认";
            this.okButton.UseVisualStyleBackColor = true;
            this.okButton.Click += new System.EventHandler(this.okButton_Click);
            // 
            // cancelButton
            // 
            this.cancelButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Location = new System.Drawing.Point(324, 278);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(90, 28);
            this.cancelButton.TabIndex = 6;
            this.cancelButton.Text = "取消";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            // 
            // ManualTokenInputForm
            // 
            this.AcceptButton = this.okButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(434, 318);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.okButton);
            this.Controls.Add(this.tokenUrlTextBox);
            this.Controls.Add(this.tokenUrlLabel);
            this.Controls.Add(this.openBrowserButton);
            this.Controls.Add(this.copyUrlButton);
            this.Controls.Add(this.instructionsLabel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "ManualTokenInputForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "网易国服授权";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label instructionsLabel;
        private System.Windows.Forms.Button copyUrlButton;
        private System.Windows.Forms.Button openBrowserButton;
        private System.Windows.Forms.Label tokenUrlLabel;
        private System.Windows.Forms.TextBox tokenUrlTextBox;
        private System.Windows.Forms.Button okButton;
        private System.Windows.Forms.Button cancelButton;
    }
}
