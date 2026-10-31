using System.Net.Http.Handlers;

namespace SCPSL_ModPatch
{
    public partial class DownloadForm : Form
    {
        CancellationTokenSource _downloadCancellationTokenSource = new();
        CancellationToken DownloadCancellationToken { get => _downloadCancellationTokenSource.Token; }
        readonly string _url;
        readonly string _fileName;
        readonly string _folderPath;

        public DownloadForm(string url, string filePath)
        {
            InitializeComponent();
            FormClosing += DownloadForm_FormClosing;
            Shown += DownloadForm_Shown;
            _url = url;
            _fileName = Path.GetFileName(filePath);
            _folderPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            fileNameTextBox.Text = string.Format(fileNameTextBox.Text, _fileName, url);
        }

        private void DownloadForm_Shown(object? sender, EventArgs e)
        {
            DownloadFile(_url, _fileName, _folderPath);
        }

        private async void DownloadFile(string url, string fileName, string folderPath)
        {
            var httpClientHandler = new HttpClientHandler() { AllowAutoRedirect = true };
            var progressMessageHandler = new ProgressMessageHandler(httpClientHandler);

            progressMessageHandler.HttpReceiveProgress += (object? sender, HttpProgressEventArgs e) =>
            {
                try
                {
                    Invoke(() => downloadProgressBar.Value = e.ProgressPercentage);
                }
                catch (Exception ex) when (ex is ObjectDisposedException) { return; }
            };

            var httpClient = new HttpClient(progressMessageHandler);
            byte[] fileData;
            START_DOWNLOAD:
            try
            {
                fileData = await httpClient.GetByteArrayAsync(url, DownloadCancellationToken);
            }
            catch (Exception ex) when (ex is TaskCanceledException) { return; }
            catch (Exception ex)
            {
                DialogResult downloadDialogResult = MessageBox.Show($"An error occurred downloading the file.\r\n" +
                    $"\r\n" +
                    $"Details: {ex.Message}", "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                if (downloadDialogResult == DialogResult.Retry)
                    goto START_DOWNLOAD;
                else
                {
                    if (!IsDisposed)
                        Close();
                    return;
                }
            }
            Directory.CreateDirectory(folderPath);
            string filePath = Path.Combine(folderPath, fileName);
            await File.WriteAllBytesAsync(filePath, fileData);
            if (!IsDisposed)
                Close();
        }

        private void CancelDownloading()
        {
            _downloadCancellationTokenSource.Cancel();
        }

        private void DownloadForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            CancelDownloading();
        }

        private void cancelDownloadingButton_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
