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
        readonly Func<IProgress<int>, CancellationToken, Task>? _downloadAction;

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

        internal DownloadForm(string url, string filePath, Func<IProgress<int>, CancellationToken, Task> downloadAction)
            : this(url, filePath)
        {
            _downloadAction = downloadAction;
        }

        private async void DownloadForm_Shown(object? sender, EventArgs e)
        {
            if (_downloadAction == null)
            {
                DownloadFile(_url, _fileName, _folderPath);
                return;
            }

            var progress = new Progress<int>(value =>
            {
                if (!IsDisposed)
                    downloadProgressBar.Value = Math.Clamp(value, 0, 100);
            });
            while (!DownloadCancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _downloadAction(progress, DownloadCancellationToken);
                    if (!IsDisposed && !DownloadCancellationToken.IsCancellationRequested)
                        DialogResult = DialogResult.OK;
                    return;
                }
                catch (OperationCanceledException) when (DownloadCancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (IsDisposed || DownloadCancellationToken.IsCancellationRequested)
                        return;
                    if (MessageBox.Show(this, $"Unlicense could not be downloaded or verified.\r\n\r\n{ex.Message}\r\n\r\n" +
                        "You can also select a local Unlicense executable in Open settings.", "Download failed",
                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry)
                        continue;
                    DialogResult = DialogResult.Cancel;
                    return;
                }
            }
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
