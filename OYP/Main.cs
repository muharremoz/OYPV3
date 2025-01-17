using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Controls;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace OYP
{
    public partial class Main : XtraForm
    {
        private DriveService _service;
        private System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public Main()
        {
            this.Icon = Properties.Resources.logo; // logo.ico dosyasının Resources'a eklenmiş olduğunu varsayıyoruz

            InitializeComponent();

            this.Text = "OYP - V:" + currentVersion;
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            ToolStripMenuItem showItem = new ToolStripMenuItem("Göster", null, ShowForm);
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Çıkış", null, ExitApplication);
            contextMenu.Items.Add(showItem);
            contextMenu.Items.Add(exitItem);

            notifyIcon1.ContextMenuStrip = contextMenu;
            notifyIcon1.Icon = Properties.Resources.logo; // İkon adınızı kullanın
            notifyIcon1.Text = "Otomatik Yedekleme Programı";
            notifyIcon1.Visible = true;

            // Timer ayarları
            timer.Interval = 900000; // 15 dakika (milisaniye cinsinden)
            timer.Tick += Timer_Tick;
            timer.Start();

        }
        private static System.Timers.Timer updateTimer;
        private bool isAutomaticBackup = false;
        private string connectionString = "Server=oypwan.bilkar.net;Database=OYP;User Id=sa;Password=P67S96L332008%;";
        private const string currentVersion = "1.0.3"; // Şu anki sürümünüz

        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdateCompanyStatus();
        }

        private void UpdateCompanyStatus()
        {
            string companyName = txt_Company.Text.Trim();

            if (string.IsNullOrEmpty(companyName))
            {
                MessageBox.Show("Firma adı boş olamaz!", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                string query = "UPDATE customers SET last_online = @online WHERE company_name = @company_name";

                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    // Tarih ve saat bilgisini alıp parametreye ekliyoruz
                    command.Parameters.AddWithValue("@online", DateTime.Now);
                    command.Parameters.AddWithValue("@company_name", companyName);

                    try
                    {
                        connection.Open();
                        int rowsAffected = command.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            //XtraMessageBox.Show($"Firma durumu başarıyla güncellendi: {companyName}", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            //XtraMessageBox.Show("Firma bulunamadı.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        XtraMessageBox.Show($"Hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void AuthenticateToGoogleDrive()
        {
            LogManager.WriteLog("Google Drive bağlantısı başladı.");
            try
            {
                UserCredential credential;

                // Programın çalıştığı dizinde drive.json dosyasını arıyoruz
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drive.json");

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    string credPath = "token.json";
                    credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.FromStream(stream).Secrets,
                        new[] { DriveService.Scope.Drive },
                        "user",
                        CancellationToken.None,
                        new FileDataStore(credPath, true)).Result;
                }

                _service = new DriveService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Google Drive Uploader",
                });
                LogManager.WriteLog("Google Drive bağlantısı başarılı.");

            }
            catch (Exception ex)
            {
                LogManager.WriteLog("Google Drive bağlantı hatası:" + ex.Message);
                throw; // Hatanın üst seviyeye iletilmesini istiyorsanız bu satırı bırakabilirsiniz.
            }
            LogManager.WriteLog("Google Drive bağlantısı bitti.");

        }

        private async Task UploadFileToGoogleDrive(string filePath)
        {
            LogManager.WriteLog("Google Drive yükleme başladı");
            // Google Drive klasör ID'sini Settings dosyasından alıyoruz
            string folderId = txtDriveFolderID.Text;

            try
            {
                var fileMetadata = new Google.Apis.Drive.v3.Data.File
                {
                    Name = Path.GetFileName(filePath),
                    Parents = new List<string> { folderId } // Hedef klasör ID'si
                };

                using (var stream = new FileStream(filePath, FileMode.Open))
                {
                    var request = _service.Files.Create(fileMetadata, stream, GetMimeType(filePath));
                    request.Fields = "id";
                    var response = await request.UploadAsync();

                    if (response.Status == Google.Apis.Upload.UploadStatus.Failed)
                    {
                        throw new Exception($"Google Drive yükleme hatası: {response.Exception}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.WriteLog("Google Drive yükleme hatası:" + ex.Message);
                throw;
            }
        }

        private string GetMimeType(string fileName)
        {
            var mimeType = "application/unknown";
            var extension = Path.GetExtension(fileName)?.ToLower();
            var registryKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(extension);
            if (registryKey?.GetValue("Content Type") != null)
            {
                mimeType = registryKey.GetValue("Content Type").ToString();
            }
            return mimeType;
        }

        public async void BackupDatabases(DateTime? backupTime = null, string backupSuffix = "", string backupFlag = "", bool isAutomatic = false, bool sendEmail = true)
        {
            if (_service == null)
                AuthenticateToGoogleDrive();

            LogManager.WriteLog("Yedekleme işlemi başlatıldı.");
            try
            {
                UpdateUI(() => navigationFrame1.SelectedPage = navigationPage3);
                await Task.Delay(1000).ConfigureAwait(false);

                int totalSteps = cmb_Database.Properties.Items.Count + cmb_Parametre.Properties.Items.Count;
                int currentStep = 0;

                UpdateUI(() =>
                {
                    progressBar.Properties.Maximum = totalSteps;
                    progressBar.EditValue = 0;
                });

                // Firma adındaki boşlukları _ ile değiştir
                string companyName = Properties.Settings.Default.CompanyName;
                companyName = companyName.Replace(" ", "_"); // Boşlukları _ ile değiştiriyoruz

                // SQL bağlantı bilgilerini Settings.settings dosyasından alıyoruz
                string server = Properties.Settings.Default.SqlServer;
                string user = Properties.Settings.Default.SqlUser;
                string password = Properties.Settings.Default.SqlPassword;
                string connectionString = $"Server={server};User Id={user};Password={password};Connection Timeout=1800;";

                // Yedek dosyasının ismini firma adı, veritabanı adı, tarih ve saatle oluşturuyoruz
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

                // Veritabanı yedekleme işlemleri
                foreach (CheckedListBoxItem item in cmb_Database.Properties.Items)
                {
                    if (item.CheckState == CheckState.Checked)
                    {
                        string databaseName = item.Value.ToString();

                        // Yedek dosyasının ismini oluşturuyoruz
                        string backupFileName = $"{txt_BackupFolder.Text}\\{databaseName}_{companyName}_{timestamp}.bak";

                        UpdateUI(() => lbl_Status.Text = $"{databaseName} veritabanı yedekleniyor...");
                        Application.DoEvents();

                        await Task.Run(async () =>
                        {
                            using (SqlConnection connection = new SqlConnection(connectionString))
                            {
                                connection.Open();
                                SqlCommand command = new SqlCommand($"BACKUP DATABASE [{databaseName}] TO DISK='{backupFileName}'", connection);
                                command.CommandTimeout = 1800;
                                await command.ExecuteNonQueryAsync();
                            }
                        }).ConfigureAwait(false);

                        currentStep++;
                        UpdateUI(() =>
                        {
                            progressBar.EditValue = currentStep;
                            lbl_Bar.Text = $"{(double)currentStep / totalSteps * 100:0.00}%";
                        });
                        LogManager.WriteLog("Yedek alınan veritabanı: " + databaseName);
                    }
                }

                // Parametre dosyası yedekleme işlemleri
                string parameterFilePath = string.Empty;
                foreach (CheckedListBoxItem paramItem in cmb_Parametre.Properties.Items)
                {
                    if (paramItem.CheckState == CheckState.Checked)
                    {
                        switch (paramItem.Value.ToString())
                        {
                            case "Perakende":
                                parameterFilePath = @"C:\\Pusula\\PerakendeSQL\\Parametre.txt";
                                break;
                            case "Toptan":
                                parameterFilePath = @"C:\\Pusula\\ToptanSQL\\TptParametre.txt";
                                break;
                            case "Üretim":
                                parameterFilePath = @"C:\\Pusula\\UretimSQL\\UrtParametre.txt";
                                break;
                            case "StokCari":
                                parameterFilePath = @"C:\\Pusula\\StokCariSQL\\StokCariParametre.txt";
                                break;
                        }

                        UpdateUI(() => lbl_Status.Text = $"{paramItem.Value} parametre dosyası yedekleniyor...");
                        Application.DoEvents();

                        if (!string.IsNullOrEmpty(parameterFilePath) && File.Exists(parameterFilePath))
                        {
                            string destinationFilePath = $"{txt_BackupFolder.Text}\\Parametre_{paramItem.Value}_{backupSuffix}_{companyName}_{timestamp}.txt";
                            File.Copy(parameterFilePath, destinationFilePath, true);
                        }

                        currentStep++;
                        UpdateUI(() =>
                        {
                            progressBar.EditValue = currentStep;
                            lbl_Bar.Text = $"{(double)currentStep / totalSteps * 100:0.00}%";
                        });
                        LogManager.WriteLog("Parametre Yedeklendi.");
                    }
                }

                // Yedek dosyalarını zip'e ekleme işlemi
                string zipFileName = $"{txt_BackupFolder.Text}\\{companyName}_{timestamp}_{backupSuffix}.zip";

                LogManager.WriteLog($"ZIP dosyası oluşturma işlemi başlatıldı: {zipFileName}");

                try
                {
                    await Task.Run(() =>
                    {
                        using (ZipArchive zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                        {
                            foreach (string file in Directory.GetFiles(txt_BackupFolder.Text, "*.bak"))
                            {
                                zip.CreateEntryFromFile(file, Path.GetFileName(file));
                                LogManager.WriteLog($"Dosya ZIP'e eklendi: {file}");
                            }

                            foreach (string file in Directory.GetFiles(txt_BackupFolder.Text, $"Parametre_*_{backupSuffix}_{timestamp}.txt"))
                            {
                                zip.CreateEntryFromFile(file, Path.GetFileName(file));
                                LogManager.WriteLog($"Dosya ZIP'e eklendi: {file}");
                            }
                        }
                    }).ConfigureAwait(false);

                    LogManager.WriteLog($"ZIP dosyası başarıyla oluşturuldu: {zipFileName}");
                }
                catch (Exception ex)
                {
                    LogManager.WriteLog($"Hata oluştu: {ex.Message}");
                }

                // Geçici dosyaları silme işlemi
                foreach (string file in Directory.GetFiles(txt_BackupFolder.Text, "*.bak"))
                {
                    File.Delete(file);
                    LogManager.WriteLog("Geçici dosya silindi." + file);
                }

                foreach (string file in Directory.GetFiles(txt_BackupFolder.Text, $"Parametre_*_{backupSuffix}_{timestamp}.txt"))
                {
                    File.Delete(file);
                    LogManager.WriteLog("Geçici parametre silindi." + file);
                }

                UpdateUI(() => lbl_Status.Text = "Yedek sunucuya gönderiliyor...");

                // Google Drive'a yükleme işlemi
                await UploadFileToGoogleDrive(zipFileName).ConfigureAwait(false);

                UpdateUI(() =>
                {
                    lbl_Status.Text = "Yedekleme işlemi tamamlandı.";
                    lbl_LastBackup.Text = DateTime.Now.ToString("dd.MM.yyyy / HH:mm");
                });
                LogManager.WriteLog("Yedekleme Tamamlandı." + "Dosya Adı: " + zipFileName);

                Properties.Settings.Default.LastBackupTime = DateTime.Now;
                Properties.Settings.Default.Save();

                if (isAutomatic)
                {
                    string updateColumn = backupFlag == "backup_1" ? "backup_1" : "backup_2";
                    string sql = $"UPDATE customers SET {updateColumn} = @flagValue, last_backup = @lastBackup WHERE company_name = @companyName";
                    string connectionString2 = "Server=oypwan.bilkar.net;Database=OYP;User Id=sa;Password=P67S96L332008%;";

                    await Task.Run(() =>
                    {
                        using (SqlConnection connection = new SqlConnection(connectionString2))
                        {
                            connection.Open();
                            SqlCommand command = new SqlCommand(sql, connection);
                            command.Parameters.AddWithValue("@flagValue", 1);
                            command.Parameters.AddWithValue("@lastBackup", DateTime.Now);
                            command.Parameters.AddWithValue("@companyName", companyName);
                            command.CommandTimeout = 1800;
                            command.ExecuteNonQuery();
                        }
                    });
                }

                UpdateUI(() => navigationFrame1.SelectedPage = navigationPage1);

                if (!isAutomatic)
                {
                    XtraMessageBox.Show("Yedekleme işlemi başarıyla tamamlandı.", "Yedekleme Tamamlandı", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                
                LogManager.WriteLog("Yedekleme işlemi tamamlandı.");

                // E-posta gönderimini sendEmail parametresine göre kontrol ediyoruz.
                if (sendEmail)
                {
                    await SendBackupCompletionEmail(isAutomatic, zipFileName);
                }
            }
            catch (Exception ex)
            {
                XtraMessageBox.Show($"Hata: {ex.Message}", "Yedekleme Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogManager.WriteLog($"Yedekleme hatası: {ex.Message}");
            }

        }

        public void UpdateBackupStatus()
        {
            LogManager.WriteLog("Son backup tarihi yazılıyor.");
            try
            {
                // Bugünün tarihini al
                DateTime today = DateTime.Today;

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    // Güncelleme sorgusu
                    string query = @"
                    UPDATE customers
                    SET backup_1 = 0, backup_2 = 0
                    WHERE CAST(last_backup AS DATE) < @Today";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        // Parametreyi ekle
                        command.Parameters.AddWithValue("@Today", today);

                        // Sorguyu çalıştır
                        int rowsAffected = command.ExecuteNonQuery();
                        LogManager.WriteLog("Backup_1 ve Backup_2 kayıtları sıfırlandı.");

                    }
                }
                LogManager.WriteLog("son backup tarihi yazıldı. " + today);
            }
            catch (Exception ex)
            {
                // Hata durumunda konsola yaz
                LogManager.WriteLog("Backup_1 ve Backup_2 kayıtları sıfırlanırken hata oldu: " + ex.Message);
            }
        }

        private void Main_Load(object sender, EventArgs e)
        {
            txtDriveFolderID.Text = Properties.Settings.Default.DriveFolderID;
            string savedCompanyName = Properties.Settings.Default.CompanyName;
            txt_Company.Text = !string.IsNullOrEmpty(savedCompanyName) ? savedCompanyName : "";
            bool ftpConnected = GetFtpCredentialsAndSave();
            bool isCompanyNameEmpty = string.IsNullOrEmpty(savedCompanyName);

            if (!ftpConnected || isCompanyNameEmpty)
            {
                // Eğer FTP bağlantısı yapılmamışsa veya firma adı boşsa ikinci sayfayı aç
                navigationFrame1.SelectedPage = navigationPage2;
                //XtraMessageBox.Show("Firma bilgilerinde hata var . Lütfen Bilgileri düzeltin.", "Eksik Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {

                StartBackupScheduler();
                LoadSQLData();
                SetAutoStart(true);
                StartActiveStatusUpdater();
                Task.Run(async () => await CheckLicenseExpiry());
                UpdateActiveStatus();
                UpdateBackupStatus();

            }


            timer1.Interval = 24 * 60 * 60 * 1000; // 24 saat (milisaniye cinsinden)
            timer1.Enabled = true;
            timer1.Tick += timer1_Tick;

            txt_SqlServer.Text = Properties.Settings.Default.SqlServer;
            txt_SqlUser.Text = Properties.Settings.Default.SqlUser;
            txt_SqlPassword.Text = Properties.Settings.Default.SqlPassword;

            if (!string.IsNullOrEmpty(txt_SqlServer.Text) && !string.IsNullOrEmpty(txt_SqlUser.Text) && !string.IsNullOrEmpty(txt_SqlPassword.Text))
            {
                ConnectToDatabase();
            }

            string[] databases = Properties.Settings.Default.DatabaseSelections.Split(',');
            foreach (CheckedListBoxItem item in cmb_Database.Properties.Items)
            {
                if (databases.Contains(item.Value.ToString()))
                {
                    item.CheckState = CheckState.Checked;
                }
            }

            string[] parameters = Properties.Settings.Default.ParameterSelections.Split(',');
            foreach (CheckedListBoxItem item in cmb_Parametre.Properties.Items)
            {
                if (parameters.Contains(item.Value.ToString()))
                {
                    item.CheckState = CheckState.Checked;
                }
            }

            txt_BackupFolder.Text = Properties.Settings.Default.BackupFolder;

            //lbl_LastBackup.Text = Properties.Settings.Default.LastBackupTime.ToString("dd-MM-yyyy HH:mm:ss");

        }

        private bool GetFtpCredentialsAndSave()
        {
            LogManager.WriteLog(txt_Company.Text + " Firmasının bilgileri alınıyor.");
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    string query = "USE [OYP]; " +
                                   "SELECT contact_email, backup1time, backup2time, last_backup, license, license_expiry, representative, representative_email, active, googleDrive " +
                                   "FROM customers WHERE company_name = @company_name";
                    SqlCommand command = new SqlCommand(query, connection);
                    command.Parameters.AddWithValue("@company_name", txt_Company.Text);

                    SqlDataReader reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        string mail = reader["contact_email"].ToString();
                        string backupTime1 = reader["backup1time"].ToString();
                        string backupTime2 = reader["backup2time"].ToString();
                        string license = reader["license"].ToString();
                        string licenseExpiry = reader["license_expiry"].ToString();
                        string temsilciAdi = reader["representative"].ToString();
                        string temsilciMail = reader["representative_email"].ToString();
                        string lastBackupTime = reader["last_backup"].ToString();
                        bool isActive = Convert.ToBoolean(reader["active"]); // active durumu
                        string googleDrive = reader["googleDrive"].ToString(); // Google Drive klasör ID'si

                        // Firma aktif değilse işlemi sonlandır
                        if (!isActive)
                        {
                            lbl_FTPStatus.Text = "Firma aktif değil.";
                            lbl_FTPStatus.ForeColor = Color.IndianRed;
                            LogManager.WriteLog("Firma aktif değil.");
                            return false; // Firma aktif değil
                        }

                        // Lisans süresi kontrolü
                        if (DateTime.TryParse(licenseExpiry, out DateTime expiryDate))
                        {
                            if (expiryDate < DateTime.Now)
                            {
                                lbl_FTPStatus.Text = "Lisans bağlantısı başarısız.";
                                lbl_license_expiry.Text = "Lisans süresi dolmuş.";
                                lbl_FTPStatus.ForeColor = Color.IndianRed;
                                lbl_license_expiry.ForeColor = Color.IndianRed;
                                LogManager.WriteLog("Lisans süresi dolmuş");
                                XtraMessageBox.Show("Lisans süresi dolmuş.", "Lisans Süresi Doldu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                return false; // Lisans süresi dolmuş
                            }
                        }
                        else
                        {
                            lbl_FTPStatus.Text = "Lisans bilgisi geçersiz.";
                            lbl_FTPStatus.ForeColor = Color.IndianRed;
                            LogManager.WriteLog("Geçersiz lisans bilgisi.");
                            XtraMessageBox.Show("Geçersiz lisans bilgisi.", "Lisans Hatası", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return false; // Lisans bilgisi geçersiz
                        }

                        // Google Drive Folder ID'sini txtDriveFolderID alanına yazdır
                        txtDriveFolderID.Text = googleDrive;

                        // Veritabanından alınan diğer verilerle ilgili alanları doldur
                        txt_Mail.Text = mail;
                        BackupTime1.Text = backupTime1;
                        BackupTime2.Text = backupTime2;
                        lbl_license.Text = license;
                        lbl_license_expiry.Text = licenseExpiry;
                        lbl_temsilciAdi.Text = temsilciAdi;
                        lbl_temsilciMail.Text = temsilciMail;
                        lbl_LastBackup.Text = lastBackupTime;

                        Properties.Settings.Default.CompanyName = txt_Company.Text;
                        Properties.Settings.Default.Save();

                        lbl_FTPStatus.Text = "Firma aktif.";
                        lbl_FTPStatus.ForeColor = Color.DarkGreen;

                        txt_Company.Enabled = false;

                        LogManager.WriteLog(txt_Company.Text + " Firmasının bilgileri alındı.");
                        return true; // İşlem başarılı
                    }
                    else
                    {
                        lbl_FTPStatus.Text = "Firma bağlantısı yapılmamış.";
                        lbl_FTPStatus.ForeColor = Color.IndianRed;
                        return false; // Firma adı bulunamadı
                    }
                }
            }
            catch (SqlException ex)
            {
                string errorMessage = $"Bağlantı Hatası: {ex.Message}\n\nBağlantı Bilgileri:\nSunucu: oypwan.bilkar.net\nKullanıcı: sa";
                XtraMessageBox.Show(errorMessage, "Bağlantı Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogManager.WriteLog("Bağlantı Hatası:" + ex.Message);
                return false; // Hata durumunda false döndür
            }
        }

        private async Task SendBackupCompletionEmail(bool isAutomatic, string zipFileName)
        {
            try
            {
                string fromEmail = "otomatikyedekleme@gmail.com";
                string fromPassword = "gvehwcwufuyjajws";
                string toEmail1 = txt_Mail.Text; // İlk alıcı
                                                 // string toEmail2 = lbl_temsilciMail.Text; // İkinci alıcı
                string subject = "Yedekleme İşlemi Tamamlandı";

                string companyName = Properties.Settings.Default.CompanyName;

                // System.IO.FileInfo ile yedek dosyasının boyutunu alıyoruz
                System.IO.FileInfo backupFileInfo = new System.IO.FileInfo(zipFileName);
                double backupFileSizeMB = backupFileInfo.Length / (1024.0 * 1024.0); // MB olarak

                string body = $@"
                <html>
                <body style='font-family: Arial, sans-serif;'>
                    <h2 style='text-align: center;'>Yedekleme Durum Raporu</h2>
                    <table border='1' cellpadding='5' cellspacing='0' style='border-collapse: collapse; width: 50%; margin: 0 auto;'>
                        <tr style='background-color: #f2f2f2;'>
                            <th style='text-align: center;'>Firma Adı</th>
                            <th style='text-align: center;'>Yedek Dosya Adı</th>
                            <th style='text-align: center;'>Boyut</th>
                        </tr>
                        <tr>
                            <td style='text-align: center;'>{companyName}</td>
                            <td style='text-align: center;'>{Path.GetFileName(zipFileName)}</td>
                            <td style='text-align: center;'>{backupFileSizeMB:0.00} MB</td>
                        </tr>
                    </table>
                    <p style='text-align: center; font-weight: bold; color: green;'>Yedekleme işlemi başarıyla tamamlanmıştır.</p>
                </body>
                </html>";

                MailMessage mail = new MailMessage();
                mail.From = new MailAddress(fromEmail);
                mail.To.Add(toEmail1); // İlk alıcıyı ekle
                                       // mail.To.Add(toEmail2); // İkinci alıcıyı ekle
                mail.Subject = subject;
                mail.Body = body;
                mail.IsBodyHtml = true;

                SmtpClient smtpClient = new SmtpClient("smtp.gmail.com", 587);
                smtpClient.Credentials = new NetworkCredential(fromEmail, fromPassword);
                smtpClient.EnableSsl = true;

                await smtpClient.SendMailAsync(mail);
                LogManager.WriteLog("Yedekleme tamamlandıktan sonra e-posta gönderildi.");
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"E-posta gönderim hatası: {ex.Message}");
                XtraMessageBox.Show($"E-posta gönderim hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public class BackupFileInfo
        {
            public string Backup1 { get; set; }
            public string Backup2 { get; set; }
            public DateTime CreationDate { get; set; }
        }

        private void LoadSQLData()
        {
            // Bağlantı dizesi ve bağlantıyı başlatma
            string connectionString = "Server=oypwan.bilkar.net;Database=OYP;User Id=sa;Password=P67S96L332008%;";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    // SQL sorgusu
                    string query = "SELECT contact_email, backup1time, backup2time, license_expiry, license, representative, representative_email " +
                                   "FROM customers WHERE company_name = @company_name";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        // Şirket adını parametre olarak ekleme
                        command.Parameters.AddWithValue("@company_name", Properties.Settings.Default.CompanyName);

                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            // Veriyi okuma ve ilgili alanlara atama
                            if (reader.Read())
                            {
                                txt_Mail.Text = reader["contact_email"].ToString();

                                // backup1time ve backup2time için DateTime türüne çevirme
                                if (DateTime.TryParse(reader["backup1time"].ToString(), out DateTime backup1Time))
                                {
                                    BackupTime1.Time = backup1Time;
                                }

                                if (DateTime.TryParse(reader["backup2time"].ToString(), out DateTime backup2Time))
                                {
                                    BackupTime2.Time = backup2Time;
                                }

                                // license_expiry için sadece tarih kısmını alma
                                if (DateTime.TryParse(reader["license_expiry"].ToString(), out DateTime licenseExpiry))
                                {
                                    lbl_license_expiry.Text = licenseExpiry.ToString("dd-MM-yyyy");
                                }

                                lbl_license.Text = reader["license"].ToString();
                                lbl_temsilciAdi.Text = reader["representative"].ToString();
                                lbl_temsilciMail.Text = reader["representative_email"].ToString();
                            }
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                XtraMessageBox.Show($"SQL Hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogManager.WriteLog("SQL Hatası: " + ex.Message);
            }
        }

        private async Task CheckLicenseExpiry()
        {
            try
            {
                if (DateTime.TryParse(lbl_license_expiry.Text, out DateTime licenseExpiry))
                {
                    TimeSpan timeRemaining = licenseExpiry - DateTime.Now;

                    // Lisans süresi bitmişse
                    if (timeRemaining.TotalDays < 0)
                    {
                        // Lisans süresi bitmişse hemen e-posta gönder
                        await SendLicenseExpiryNotification("Lisans süresi bitmiştir.");
                    }
                    // Eğer lisans süresine 7 gün veya daha az kalmışsa
                    else if (timeRemaining.TotalDays <= 7)
                    {
                        await SendLicenseExpiryNotification($"lisans süresinin bitmesine {timeRemaining.Days} gün kalmıştır.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"Lisans kontrolü sırasında hata: {ex.Message}");
                XtraMessageBox.Show($"Lisans kontrolü sırasında hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task SendLicenseExpiryNotification(string message)
        {
            try
            {
                string fromEmail = "otomatikyedekleme@gmail.com";
                string fromPassword = "gvehwcwufuyjajws";
                string toEmail = lbl_temsilciMail.Text; // Temsilci e-posta

                string companyName = Properties.Settings.Default.CompanyName; // Firmanın adı
                string representativeName = lbl_temsilciAdi.Text; // Temsilcinin adı
                string subject = $"{companyName} - Lisans Süresi Hakkında Uyarı";

                string body = $@"
        <html>
        <body style='font-family: Arial, sans-serif; margin: 0; padding: 0; background-color: #f4f4f4;'>
        
            <!-- Başlık kısmı -->
            <table style='width: 100%; background-color: #333; padding: 40px 0;'>
                <tr>
                    <td style='text-align: center; color: #fff;'>
                        <h1 style='margin: 0; font-size: 30px;'>Lisans Süresi Hakkında Uyarı</h1>
                    </td>
                </tr>
            </table>

            <!-- Mesaj içeriği -->
            <table style='width: 100%; max-width: 600px; margin: 30px auto; background-color: #fff; padding: 30px; border-radius: 8px; box-shadow: 0 4px 10px rgba(0, 0, 0, 0.1);'>
                <tr>
                    <td style='font-size: 18px; color: #333; text-align: center;'>
                        <p style='font-size: 22px; font-weight: bold; color: #e74c3c;'>Sayın {representativeName},</p>
                        <p style='font-size: 16px; color: #7f8c8d;'>{companyName} firmasının {message}</p>
                        <p style='font-size: 16px; color: #7f8c8d;'>Lütfen firma ile görüşmeyi unutmayınız.</p>
                    </td>
                </tr>
            </table>

            <!-- Alt kısım -->
            <table style='width: 100%; max-width: 600px; margin: 30px auto; background-color: #333; padding: 20px 0; border-radius: 8px;'>
                <tr>
                    <td style='text-align: center; color: #fff; font-size: 14px;'>
                        <p>© {DateTime.Now.Year} Bilkar Bilgisayar. Tüm hakları saklıdır.</p>
                    </td>
                </tr>
            </table>

        </body>
        </html>";

                MailMessage mail = new MailMessage();
                mail.From = new MailAddress(fromEmail);
                mail.To.Add(toEmail);
                mail.Subject = subject;
                mail.Body = body;
                mail.IsBodyHtml = true;

                SmtpClient smtpClient = new SmtpClient("smtp.gmail.com", 587);
                smtpClient.Credentials = new NetworkCredential(fromEmail, fromPassword);
                smtpClient.EnableSsl = true;

                await smtpClient.SendMailAsync(mail);

                LogManager.WriteLog($"Lisans süresi uyarısı e-posta olarak gönderildi: {message}");
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"Lisans süresi e-posta gönderim hatası: {ex.Message}");
                XtraMessageBox.Show($"Lisans süresi e-posta gönderim hatası: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartActiveStatusUpdater()
        {
            // Timer oluşturuluyor ve 5 dakikada bir çalışacak şekilde ayarlanıyor
            updateTimer = new System.Timers.Timer(1800000); // 30dk
            updateTimer.Elapsed += (sender, e) => UpdateActiveStatus(); // Her tetiklemede metodu çağırır
            updateTimer.AutoReset = true; // Sürekli tekrar eder
            updateTimer.Start();
        }

        private void UpdateActiveStatus()
        {
            string connectionString = "Server=oypwan.bilkar.net;Database=OYP;User Id=sa;Password=P67S96L332008%;";

            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    // SQL sorgusu
                    string query = @"
                UPDATE customers
                SET active = 1, last_active = @currentDateTime
                WHERE company_name = @companyName";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        // Parametreler ekleniyor
                        command.Parameters.AddWithValue("@currentDateTime", DateTime.Now);
                        command.Parameters.AddWithValue("@companyName", Properties.Settings.Default.CompanyName);

                        // Sorgu çalıştırılıyor
                        int rowsAffected = command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                XtraMessageBox.Show($"UpdateActiveStatus | Hata: {ex.Message}");
                LogManager.WriteLog("UpdateActiveStatus | Hata: " + ex.Message);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            this.Hide(); // Formu gizler
            notifyIcon1.BalloonTipTitle = "Yedekleme Programı Başladı";
            notifyIcon1.BalloonTipText = "Belirtilen saatlerde yedek alınacaktır. Lütfen Programı Kapatmayın.";
            notifyIcon1.ShowBalloonTip(1000);
        }

        private void ShowForm(object sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
        }

        private void ExitApplication(object sender, EventArgs e)
        {
            using (password passwordForm = new password())
            {
                var result = passwordForm.ShowDialog();

                if (result == DialogResult.OK && passwordForm.EnteredPassword == "Bilkar")
                {
                    notifyIcon1.Visible = false; // NotifyIcon'u gizle
                    Environment.Exit(0); // Uygulamayı kapat
                }
                else if (result == DialogResult.Cancel)
                {
                    // Kullanıcı İptal'e bastıysa hiçbir işlem yapma
                    return;
                }
                else
                {
                    MessageBox.Show(
                        "Hatalı şifre! Çıkış yapılamadı.",
                        "Şifre Hatası",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            e.Cancel = true; // Kapatmayı engeller
            this.Hide();
            notifyIcon1.ShowBalloonTip(500, "Yedekleme Programı", "Uygulama sistem tepsisinde çalışmaya devam ediyor.", ToolTipIcon.Info);
        }

        private void ConnectToDatabase()
        {
            string server = txt_SqlServer.Text;
            string user = txt_SqlUser.Text;
            string password = txt_SqlPassword.Text;

            // Yeni bağlantı dizesini oluşturuyoruz
            string connectionString = $"Server={server};User Id={user};Password={password};";

            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    lbl_SqlStatus.Text = "Bağlantı başarılı!";
                    lbl_SqlStatus.ForeColor = System.Drawing.Color.Green;
                    groupControl7.CustomHeaderButtons[0].Properties.Visible = false;


                    // Bağlantı bilgilerini kaydetme
                    Properties.Settings.Default.SqlServer = server;
                    Properties.Settings.Default.SqlUser = user;
                    Properties.Settings.Default.SqlPassword = password;
                    Properties.Settings.Default.Save();
                    LoadDatabases();
                    LoadParameters();
                }
            }
            catch (SqlException ex)
            {
                groupControl7.CustomHeaderButtons[0].Properties.Visible = true;
                XtraMessageBox.Show($"Bağlantı Hatası: {ex.Message}", "Bağlantı Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogManager.WriteLog("Bağlantı Hatası: " + ex.Message);
            }

        }

        public void LoadDatabases()
        {
            // Properties.Settings üzerinden bağlantı bilgilerini alıyoruz
            string connectionString = $"Server={Properties.Settings.Default.SqlServer};User Id={Properties.Settings.Default.SqlUser};Password={Properties.Settings.Default.SqlPassword};";

            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                    SqlCommand command = new SqlCommand("SELECT name FROM sys.databases WHERE name NOT IN ('master', 'tempdb', 'model', 'msdb');", connection);
                    SqlDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        cmb_Database.Properties.Items.Add(reader["name"].ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                XtraMessageBox.Show($"Veritabanları yüklenirken hata oluştu: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogManager.WriteLog("LoadDatabases Hatası: " + ex.Message);
            }
        }

        public void LoadParameters()
        {
            cmb_Parametre.Properties.Items.AddRange(new string[] { "Perakende", "Toptan", "Üretim", "Stokcari" });
        }

        private void UpdateUI(Action action)
        {
            if (InvokeRequired)
            {
                Invoke(action);
            }
            else
            {
                action();
            }
        }

        public void StartBackupScheduler()
        {
            System.Timers.Timer backupTimer = new System.Timers.Timer(1000); // 1 saniyede bir kontrol
            backupTimer.Elapsed += (sender, e) =>
            {
                DateTime now = DateTime.Now;

                // BackupTime1 saatinde ve sadece bir kere yedek al
                if (now.Hour == BackupTime1.Time.Hour && now.Minute == BackupTime1.Time.Minute && now.Second == 0)
                {
                    BackupDatabases(BackupTime1.Time, "Backup_1", "backup_1", true); // otomatik yedekleme
                }
                // BackupTime2 saatinde ve sadece bir kere yedek al
                else if (now.Hour == BackupTime2.Time.Hour && now.Minute == BackupTime2.Time.Minute && now.Second == 0)
                {
                    BackupDatabases(BackupTime2.Time, "Backup_2", "backup_2", true); // otomatik yedekleme
                }
            };
            backupTimer.Start();
        }

        private void SetAutoStart(bool enable)
        {
            string appName = "OYP.exe"; // Uygulama adınızı buraya yazın
            string appPath = Application.ExecutablePath;

            RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

            if (enable)
            {
                // Kayıt defterine uygulama yolunu ekle
                registryKey.SetValue(appName, $"\"{appPath}\"");
            }
            else
            {
                // Kayıt defterinden uygulama yolunu kaldır
                registryKey.DeleteValue(appName, false);
            }
        }

        private void txt_BackupFolder_DoubleClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    txt_BackupFolder.Text = folderDialog.SelectedPath;
                }
            }
        }

        private void btn_Save_Click(object sender, EventArgs e)
        {
            // Seçili veritabanlarını kaydetme
            List<string> selectedDatabases = new List<string>();
            foreach (CheckedListBoxItem item in cmb_Database.Properties.Items)
            {
                if (item.CheckState == CheckState.Checked)
                {
                    selectedDatabases.Add(item.Value.ToString());
                }
            }
            Properties.Settings.Default.DatabaseSelections = string.Join(",", selectedDatabases);

            // Seçili parametreleri kaydetme
            List<string> selectedParameters = new List<string>();
            foreach (CheckedListBoxItem item in cmb_Parametre.Properties.Items)
            {
                if (item.CheckState == CheckState.Checked)
                {
                    selectedParameters.Add(item.Value.ToString());
                }
            }
            Properties.Settings.Default.ParameterSelections = string.Join(",", selectedParameters);

            // Diğer alanları kaydetme
            Properties.Settings.Default.MailAddress = txt_Mail.Text;
            Properties.Settings.Default.BackupFolder = txt_BackupFolder.Text;
            Properties.Settings.Default.BackupTime1 = BackupTime1.Time.ToString();
            Properties.Settings.Default.BackupTime2 = BackupTime2.Time.ToString();

            // Ayarları kaydetme
            Properties.Settings.Default.Save();

            XtraMessageBox.Show("Ayarlar başarıyla kaydedildi.");
        }

        private void groupControl4_CustomButtonClick(object sender, DevExpress.XtraBars.Docking2010.BaseButtonEventArgs e)
        {
            // Firma adı ve FTP bilgilerini al ve kaydet
            GetFtpCredentialsAndSave();
        }

        private void groupControl1_CustomButtonClick(object sender, DevExpress.XtraBars.Docking2010.BaseButtonEventArgs e)
        {

        }

        private void groupControl7_CustomButtonClick(object sender, DevExpress.XtraBars.Docking2010.BaseButtonEventArgs e)
        {
            ConnectToDatabase();
        }

        private void Main_FormClosed_1(object sender, FormClosedEventArgs e)
        {
            LogManager.WriteLog("Program kapatıldı.");

        }

        private void lbl_license_Click(object sender, EventArgs e)
        {
            // lbl_license'ın değerini kopyala
            Clipboard.SetText(lbl_license.Text);

            // Kullanıcıya uyarı mesajı göster
            XtraMessageBox.Show("Lisans anahtarı kopyalandı!", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            ExitApplication(sender, e);
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            Task.Run(async () => await CheckLicenseExpiry()); // Lisans süresini kontrol etme
        }

        private void Main_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl + T kombinasyonunu kontrol et
            if (e.Control && e.KeyCode == Keys.T)
            {
                var result = XtraMessageBox.Show(
                    "Tüm ayarları sıfırlamak istediğinizden emin misiniz?",
                    "Ayarları Sıfırla",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );

                if (result == DialogResult.Yes)
                {
                    // Ayarları sıfırla ve kaydet
                    Properties.Settings.Default.Reset();
                    Properties.Settings.Default.Save();

                    XtraMessageBox.Show("Ayarlar varsayılana sıfırlandı.", "Başarılı", MessageBoxButtons.OK, MessageBoxIcon.Information);

                }

                // Olayın işlem gördüğünü belirterek başka bir işlem yapılmasını engelle
                e.Handled = true;
            }

            if (e.Control && e.KeyCode == Keys.P)
            {
                var result = XtraMessageBox.Show(
                    "Pusula Yazılım için SQL bağlantı bilgileri eklensin mi ?",
                    "SQL Bilgilerini Getir",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );

                if (result == DialogResult.Yes)
                {
                    // Ayarları sıfırla ve kaydet   

                    txt_SqlServer.Text = "127.0.0.1";
                    txt_SqlPassword.Text = "P67S96L332008%";
                    txt_SqlUser.Text = "sa";
                    ConnectToDatabase();
                    //XtraMessageBox.Show("Bilgileri Eklendi", "Başarılı", MessageBoxButtons.OK, MessageBoxIcon.Information);


                }

                // Olayın işlem gördüğünü belirterek başka bir işlem yapılmasını engelle
                e.Handled = true;
            }

            if (e.KeyCode == Keys.F5)
            {
                // F5 tuşuna basıldığında GetFtpCredentialsAndSave() metodunu çağır
                GetFtpCredentialsAndSave();
            }

            if (e.KeyCode == Keys.G)
            {
                txtDriveFolderID.Enabled = true;
                txt_Company.Enabled = true;
            }

            if (e.KeyCode == Keys.O)
            {
                UpdateCompanyStatus();
            }

        }

        private void lbl_web_Click(object sender, EventArgs e)
        {
            try
            {
                // URL'yi varsayılan tarayıcıda aç
                System.Diagnostics.Process.Start("https://www.bilkar.net");
            }
            catch (Exception ex)
            {
                // Hata durumunda kullanıcıya mesaj göster
                MessageBox.Show($"Web sitesi açılamadı. Hata: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void barBtnSettingSave_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e)
        {
            // Seçili veritabanlarını kaydetme
            List<string> selectedDatabases = new List<string>();
            foreach (CheckedListBoxItem item in cmb_Database.Properties.Items)
            {
                if (item.CheckState == CheckState.Checked)
                {
                    selectedDatabases.Add(item.Value.ToString());
                }
            }
            Properties.Settings.Default.DatabaseSelections = string.Join(",", selectedDatabases);

            // Seçili parametreleri kaydetme
            List<string> selectedParameters = new List<string>();
            foreach (CheckedListBoxItem item in cmb_Parametre.Properties.Items)
            {
                if (item.CheckState == CheckState.Checked)
                {
                    selectedParameters.Add(item.Value.ToString());
                }
            }
            Properties.Settings.Default.ParameterSelections = string.Join(",", selectedParameters);

            // Diğer alanları kaydetme
            Properties.Settings.Default.MailAddress = txt_Mail.Text;
            Properties.Settings.Default.BackupFolder = txt_BackupFolder.Text;
            Properties.Settings.Default.BackupTime1 = BackupTime1.Time.ToString();
            Properties.Settings.Default.BackupTime2 = BackupTime2.Time.ToString();

            // Ayarları kaydetme
            Properties.Settings.Default.Save();

            XtraMessageBox.Show("Ayarlar başarıyla kaydedildi.");
        }

        private void barBtnFTPSetting_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e)
        {
            navigationFrame1.SelectedPage = navigationPage2;
        }

        private void barBtnSetting_ItemClick_1(object sender, DevExpress.XtraBars.ItemClickEventArgs e)
        {
            navigationFrame1.SelectedPage = navigationPage1;
        }

        private void txtDriveFolderID_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) // Enter tuşuna basıldığında
            {
                try
                {
                    // Veritabanı bağlantısı
                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        connection.Open();

                        string query = "UPDATE customers SET googleDrive = @googleDrive WHERE company_name = @company_name";
                        SqlCommand command = new SqlCommand(query, connection);
                        command.Parameters.AddWithValue("@googleDrive", txtDriveFolderID.Text);
                        command.Parameters.AddWithValue("@company_name", txt_Company.Text); // Firma adıyla ilgili filtre

                        int rowsAffected = command.ExecuteNonQuery();
                        if (rowsAffected > 0)
                        {
                            // Başarılı güncelleme durumunda
                            XtraMessageBox.Show("Drive ID veritabanında güncellendi.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            // Firma bulunamazsa veya güncelleme yapılmazsa
                            XtraMessageBox.Show("Firma bulunamadı veya güncelleme başarısız oldu.", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
                catch (SqlException ex)
                {
                    string errorMessage = $"Veritabanı Hatası: {ex.Message}\n\nBağlantı Bilgileri:\nSunucu: oypwan.bilkar.net\nKullanıcı: sa";
                    XtraMessageBox.Show(errorMessage, "Veritabanı Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

        }

        private void barButtonItem1_ItemClick_1(object sender, DevExpress.XtraBars.ItemClickEventArgs e)
        {
            BackupDatabases(sendEmail: false);
        }

        private void barButtonItem3_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e)
        {
            AuthenticateToGoogleDrive();

        }
    }
}
