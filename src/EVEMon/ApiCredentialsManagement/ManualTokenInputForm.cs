using System;
using System.Windows.Forms;
using EVEMon.Common;
using EVEMon.Common.Constants;
using EVEMon.Common.Controls;
using EVEMon.Common.Extensions;

namespace EVEMon.ApiCredentialsManagement
{
    public sealed partial class ManualTokenInputForm : EVEMonForm
    {
        private readonly string m_authUrl;
        private readonly string m_state;

        public string Token { get; private set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        private ManualTokenInputForm()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="authUrl">The authorization URL to display to the user.</param>
        /// <param name="state">The state parameter for CSRF protection.</param>
        public ManualTokenInputForm(string authUrl, string state)
            : this()
        {
            m_authUrl = authUrl;
            m_state = state;
        }

        /// <summary>
        /// Occurs when the form loads.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            if (DesignMode)
                return;

            // Display instructions for Seren server (Authorization Code Flow)
            instructionsLabel.Text = @"网易国服授权流程（Authorization Code 模式）：

1. 点击下方按钮打开授权页面
2. 在浏览器中登录网易账号并授权
3. 授权成功后，浏览器会跳转到 ESI 页面
4. 复制浏览器地址栏中的完整 URL（包含 ?code=）
5. 将 URL 粘贴到下方的输入框中
6. 点击""确认""完成授权";
        }

        /// <summary>
        /// Handles the Click event of the copyUrlButton control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void copyUrlButton_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(m_authUrl);
            MessageBox.Show(@"授权链接已复制到剪贴板。", @"提示", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        /// <summary>
        /// Handles the Click event of the openBrowserButton control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void openBrowserButton_Click(object sender, EventArgs e)
        {
            // Seren server: Must clear old session first (Pyfa compatible flow)
            // See: https://login.evepc.163.com/account/logoff
            Util.OpenURL(new Uri(@"https://login.evepc.163.com/account/logoff"));
            
            // Delay before opening auth URL (Pyfa uses time.sleep(1))
            System.Threading.Thread.Sleep(1000);
            
            // Then open the auth URL
            Util.OpenURL(new Uri(m_authUrl));
        }

        /// <summary>
        /// Handles the Click event of the okButton control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void okButton_Click(object sender, EventArgs e)
        {
            string callbackUrl = tokenUrlTextBox.Text.Trim();

            if (string.IsNullOrEmpty(callbackUrl))
            {
                MessageBox.Show(@"请粘贴回调 URL。", @"提示", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Parse the callback URL to extract access_token
            string accessToken = ParseAccessToken(callbackUrl);
            if (string.IsNullOrEmpty(accessToken))
            {
                MessageBox.Show(@"无法从回调 URL 中提取 access_token。请确保复制了完整的 URL（包含 #access_token=）。", @"错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Token = accessToken;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Parses the callback URL to extract the authorization code or access token.
        /// Supports both:
        /// - Implicit Flow: #access_token=xxx
        /// - Authorization Code Flow: ?code=xxx
        /// </summary>
        /// <param name="callbackUrl">The callback URL containing the code or token.</param>
        /// <returns>The authorization code or access token, or null if not found.</returns>
        private string ParseAccessToken(string callbackUrl)
        {
            try
            {
                // First try Authorization Code Flow (?code=)
                int queryIndex = callbackUrl.IndexOf('?');
                if (queryIndex >= 0)
                {
                    string query = callbackUrl.Substring(queryIndex + 1);
                    string[] pairs = query.Split('&');
                    foreach (string pair in pairs)
                    {
                        int equalIndex = pair.IndexOf('=');
                        if (equalIndex < 0)
                            continue;

                        string key = pair.Substring(0, equalIndex);
                        if (key.Equals("code", StringComparison.OrdinalIgnoreCase))
                        {
                            return Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
                        }
                    }
                }

                // Then try Implicit Flow (#access_token=)
                int fragmentIndex = callbackUrl.IndexOf('#');
                if (fragmentIndex >= 0)
                {
                    string fragment = callbackUrl.Substring(fragmentIndex + 1);
                    string[] pairs = fragment.Split('&');
                    foreach (string pair in pairs)
                    {
                        int equalIndex = pair.IndexOf('=');
                        if (equalIndex < 0)
                            continue;

                        string key = pair.Substring(0, equalIndex);
                        if (key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
                        {
                            return Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
                        }
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Handles the Click event of the cancelButton control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void cancelButton_Click(object sender, EventArgs e)
        {
            Token = null;
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
