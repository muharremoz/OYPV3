using DevExpress.LookAndFeel;
using DevExpress.XtraEditors;
using DevExpress.XtraEditors.Designer.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OYP
{
    public partial class password : XtraForm
    {
        public string EnteredPassword { get; private set; }
        public password()
        {
            InitializeComponent();
        }

        private void btn_exit_Click(object sender, EventArgs e)
        {
            EnteredPassword = txtPassword.Text; // Kullanıcının girdiği şifreyi al
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void btn_cancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel; // Kullanıcı iptal etti
            this.Close(); // Formu kapat
        }

        private void password_Load(object sender, EventArgs e)
        {
            this.LookAndFeel.SetSkinStyle(SkinSvgPalette.WXI.Clearness);

        }
    }
}
