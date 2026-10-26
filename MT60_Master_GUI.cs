using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DelphiMT60
{
    // =========================================================================================
    //  JAM DELPHI MT60 MASTER v2 — GUI (WinForms, .NET 4.0 mos)
    //  Protokol: Delphi_MT60_Core_Protocol_v2.cs (DelphiMt60Client)
    // =========================================================================================
    public class MainForm : Form
    {
        // Ranglar (qorong'i zamonaviy mavzu)
        static readonly Color BG      = Color.FromArgb(24, 28, 36);
        static readonly Color PANEL   = Color.FromArgb(30, 35, 46);
        static readonly Color LOGBG   = Color.FromArgb(15, 17, 22);
        static readonly Color LOGFG   = Color.FromArgb(74, 222, 128);
        static readonly Color TXT     = Color.FromArgb(226, 232, 240);
        static readonly Color MUTED   = Color.FromArgb(148, 163, 184);
        static readonly Color BLUE    = Color.FromArgb(37, 99, 235);
        static readonly Color GREEN   = Color.FromArgb(22, 163, 74);
        static readonly Color RED     = Color.FromArgb(220, 38, 38);
        static readonly Color PURPLE  = Color.FromArgb(109, 40, 217);
        static readonly Color GRAY    = Color.FromArgb(71, 85, 105);

        private DelphiMt60Client _client;
        private volatile bool _connected;
        private volatile bool _busy;

        private TextBox _txtLog;
        private Label _lblAdapter;
        private J2534Device _selected;   // null = Avto (hammasini sinash)
        private List<J2534Device> _devices = new List<J2534Device>();
        private string _manualDll;
        private readonly string _cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mt60_adapter.cfg");
        private ProgressBar _bar;
        private Label _lblStatus;
        private Label _lblConn;
        private FlowLayoutPanel _btns;

        public MainForm()
        {
            Text = "JAM DELPHI MT60 MASTER v2 — Chevrolet Cobalt / Gentra / Ravon R3 (TC1766 1.5 MB)";
            BackColor = BG;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(960, 620);
            MinimumSize = new Size(820, 540);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            Log("JAM DELPHI MT60 MASTER v2 ishga tushirildi.");
            RefreshDevices();
            Log("Adapterni tanlang (avtomatik topildi) va ULANISH bosing.");
        }

        private void BuildUi()
        {
            // ---- Yuqori sarlavha ----
            Panel top = new Panel();
            top.Dock = DockStyle.Top; top.Height = 56; top.BackColor = PANEL;
            Label title = new Label();
            title.Text = "⚡ DELPHI MT60 MASTER";
            title.ForeColor = TXT; title.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            title.Location = new Point(16, 10); title.AutoSize = true;
            Label sub = new Label();
            sub.Text = "Full Flash • Pasport • VIN • EEPROM";
            sub.ForeColor = MUTED; sub.Font = new Font("Segoe UI", 8.5f);
            sub.Location = new Point(19, 36); sub.AutoSize = true;
            _lblConn = new Label();
            _lblConn.Text = "● ULANMAGAN"; _lblConn.ForeColor = RED;
            _lblConn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _lblConn.AutoSize = true; _lblConn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblConn.Location = new Point(top.Width - 140, 20);
            top.Controls.Add(title); top.Controls.Add(sub); top.Controls.Add(_lblConn);
            Controls.Add(top);

            // ---- Pastki panel (progress + status) ----
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 58; bottom.BackColor = PANEL;
            _bar = new ProgressBar();
            _bar.Location = new Point(16, 10); _bar.Size = new Size(928, 18);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _bar.Maximum = 100;
            _lblStatus = new Label();
            _lblStatus.Text = "Tayyor"; _lblStatus.ForeColor = MUTED;
            _lblStatus.Location = new Point(16, 34); _lblStatus.AutoSize = true;
            bottom.Controls.Add(_bar); bottom.Controls.Add(_lblStatus);
            Controls.Add(bottom);

            // ---- Chap panel (tugmalar) ----
            Panel left = new Panel();
            left.Dock = DockStyle.Left; left.Width = 268; left.BackColor = BG; left.Padding = new Padding(16, 12, 12, 12);

            Label dllLbl = new Label();
            dllLbl.Text = "Adapter:"; dllLbl.ForeColor = MUTED; dllLbl.AutoSize = true;
            dllLbl.Location = new Point(16, 8);
            _lblAdapter = new Label();
            _lblAdapter.Text = "aniqlanmoqda...";
            _lblAdapter.ForeColor = TXT; _lblAdapter.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _lblAdapter.Location = new Point(70, 8); _lblAdapter.Size = new Size(182, 20); _lblAdapter.AutoEllipsis = true;
            Button btnChange = new Button();
            btnChange.Text = "Adapterni almashtirish"; StyleBtn(btnChange, GRAY);
            btnChange.Location = new Point(16, 32); btnChange.Size = new Size(236, 26);
            btnChange.Click += delegate { ShowAdapterMenu(btnChange); };
            left.Controls.Add(dllLbl); left.Controls.Add(_lblAdapter); left.Controls.Add(btnChange);

            _btns = new FlowLayoutPanel();
            _btns.Location = new Point(16, 70); _btns.Size = new Size(240, 444);
            _btns.FlowDirection = FlowDirection.TopDown; _btns.WrapContents = false;
            _btns.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;

            AddBtn("1  ULANISH", BLUE, delegate { DoConnect(); });
            AddBtn("2  IDENT / VIN O'QISH", BLUE, delegate { DoIdent(); });
            AddBtn("3  FULL FLASH O'QISH", GREEN, delegate { DoRead(); });
            AddBtn("4  FULL FLASH YOZISH", RED, delegate { DoWrite(); });
            AddBtn("5  VIN YOZISH (pasport)", PURPLE, delegate { DoVin(); });
            AddBtn("6  EEPROM o'qish/yozish", GRAY, delegate { Log("[6] EEPROM — komanda kutilmoqda (keyin qo'shiladi)."); });
            AddBtn("7  ZASLONKA reset", GRAY, delegate { Log("[7] Zaslonka reset — komanda kutilmoqda (keyin qo'shiladi)."); });
            AddBtn("Log tozalash", GRAY, delegate { _txtLog.Clear(); });

            left.Controls.Add(_btns);
            Controls.Add(left);

            // ---- Markaz: log ----
            _txtLog = new TextBox();
            _txtLog.Multiline = true; _txtLog.ReadOnly = true; _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Dock = DockStyle.Fill; _txtLog.BackColor = LOGBG; _txtLog.ForeColor = LOGFG;
            _txtLog.Font = new Font("Consolas", 9f); _txtLog.BorderStyle = BorderStyle.None;
            Panel logWrap = new Panel();
            logWrap.Dock = DockStyle.Fill; logWrap.BackColor = LOGBG; logWrap.Padding = new Padding(10);
            logWrap.Controls.Add(_txtLog);
            Controls.Add(logWrap);
            logWrap.BringToFront();

            FormClosing += delegate { try { if (_client != null) _client.Dispose(); } catch { } };
        }

        private void AddBtn(string text, Color color, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text; StyleBtn(b, color);
            b.Size = new Size(232, 44); b.Margin = new Padding(0, 0, 0, 8);
            b.TextAlign = ContentAlignment.MiddleLeft; b.Padding = new Padding(12, 0, 0, 0);
            b.Click += onClick;
            _btns.Controls.Add(b);
        }

        private static void StyleBtn(Button b, Color color)
        {
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = color; b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            b.Cursor = Cursors.Hand; b.UseVisualStyleBackColor = false;
        }

        // ================= Yordamchilar =================

        private void Log(string s)
        {
            if (_txtLog.InvokeRequired) { _txtLog.BeginInvoke(new Action<string>(Log), s); return; }
            _txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, s, Environment.NewLine));
        }

        private void SetStatus(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), s); return; }
            _lblStatus.Text = s;
        }

        private void SetConnected(bool c)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetConnected), c); return; }
            _connected = c;
            _lblConn.Text = c ? "● ULANGAN" : "● ULANMAGAN";
            _lblConn.ForeColor = c ? GREEN : RED;
        }

        private void OnProgress(int off, int tot)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, int>(OnProgress), off, tot); return; }
            int pct = tot > 0 ? (int)((long)off * 100 / tot) : 0;
            if (pct < 0) pct = 0; if (pct > 100) pct = 100;
            _bar.Value = pct;
            _lblStatus.Text = string.Format("{0}%   ({1:N0} / {2:N0} bayt)", pct, off, tot);
        }

        private void RunBg(string title, ThreadStart work)
        {
            if (_busy) { Log("Avval joriy amal tugashini kuting."); return; }
            _busy = true; _btns.Enabled = false; SetStatus(title + " ...");
            Thread t = new Thread(delegate ()
            {
                try { work(); }
                catch (Exception ex) { Log("[XATO] " + ex.Message); }
                finally
                {
                    BeginInvoke(new MethodInvoker(delegate ()
                    {
                        _busy = false; _btns.Enabled = true; _bar.Value = 0;
                        if (_lblStatus.Text.EndsWith("...")) _lblStatus.Text = "Tayyor";
                    }));
                }
            });
            t.IsBackground = true; t.Start();
        }

        private void RefreshDevices()
        {
            _devices = J2534Registry.List();

            // Standart tanlov: 1) oxirgi ishlatilgan  2) Scanmatik SM2  3) birinchisi
            J2534Device pick = null;
            string saved = LoadSavedDll();
            if (!string.IsNullOrEmpty(saved))
                foreach (J2534Device d in _devices)
                    if (string.Equals(d.DllPath, saved, StringComparison.OrdinalIgnoreCase)) { pick = d; break; }
            if (pick == null)
                foreach (J2534Device d in _devices)
                    if (d.DllPath.ToLower().EndsWith("smj2534.dll")) { pick = d; break; }  // Scanmatik SM2
            if (pick == null && _devices.Count > 0) pick = _devices[0];

            SetAdapter(pick);

            if (_devices.Count > 0)
            {
                Log("Topilgan J2534 adapter(lar): " + _devices.Count);
                foreach (J2534Device d in _devices) Log("   • " + d + "   [" + d.DllPath + "]");
                if (pick != null) Log("Tanlandi: " + pick.Name + "  — endi ULANISH bosing.");
            }
            else
            {
                Log("J2534 adapter registrda topilmadi. 'Adapterni almashtirish' -> DLL... orqali ko'rsating.");
            }
        }

        private void SetAdapter(J2534Device d)
        {
            if (InvokeRequired) { BeginInvoke(new Action<J2534Device>(SetAdapter), d); return; }
            _selected = d;
            _lblAdapter.Text = (d != null && !string.IsNullOrEmpty(d.DllPath)) ? d.Name : "Avto (ulanganini top)";
        }

        // Sinash tartibi: oxirgi ishlatilgan -> Scanmatik -> qolganlar
        private List<J2534Device> OrderedCandidates()
        {
            List<J2534Device> cand = new List<J2534Device>();
            if (!string.IsNullOrEmpty(_manualDll) && File.Exists(_manualDll))
            {
                J2534Device md = new J2534Device(); md.Name = "Qo'lda DLL"; md.DllPath = _manualDll; cand.Add(md);
            }
            string saved = LoadSavedDll();
            foreach (J2534Device d in _devices)
                if (!string.IsNullOrEmpty(saved) && string.Equals(d.DllPath, saved, StringComparison.OrdinalIgnoreCase)) cand.Add(d);
            foreach (J2534Device d in _devices)
                if (d.DllPath.ToLower().EndsWith("smj2534.dll") && !cand.Contains(d)) cand.Add(d);
            foreach (J2534Device d in _devices)
                if (!cand.Contains(d)) cand.Add(d);
            return cand;
        }

        private void ShowAdapterMenu(Control anchor)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem auto = new ToolStripMenuItem("Avto (ulanganini top)");
            auto.Click += delegate { SetAdapter(null); Log("Rejim: Avto."); };
            m.Items.Add(auto);
            m.Items.Add(new ToolStripSeparator());
            foreach (J2534Device d in _devices)
            {
                J2534Device dd = d;
                ToolStripMenuItem it = new ToolStripMenuItem(d.ToString());
                it.Click += delegate { SetAdapter(dd); Log("Adapter tanlandi: " + dd.Name); };
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem refr = new ToolStripMenuItem("↻ Yangilash (qayta skaner)");
            refr.Click += delegate { RefreshDevices(); };
            m.Items.Add(refr);
            ToolStripMenuItem dll = new ToolStripMenuItem("DLL... (qo'lda ko'rsatish)");
            dll.Click += delegate
            {
                OpenFileDialog d = new OpenFileDialog(); d.Filter = "J2534 DLL|*.dll|Barcha|*.*";
                if (d.ShowDialog() == DialogResult.OK)
                {
                    _manualDll = d.FileName;
                    J2534Device md = new J2534Device(); md.Name = "Qo'lda: " + Path.GetFileName(d.FileName); md.DllPath = d.FileName;
                    SetAdapter(md); Log("Qo'lda DLL: " + d.FileName);
                }
            };
            m.Items.Add(dll);
            m.Show(anchor, new Point(0, anchor.Height));
        }

        private string LoadSavedDll()
        {
            try { return File.Exists(_cfgPath) ? File.ReadAllText(_cfgPath).Trim() : null; }
            catch { return null; }
        }

        private void SaveDll(string dll)
        {
            try { File.WriteAllText(_cfgPath, dll); } catch { }
        }

        private void SwapClient(DelphiMt60Client c)
        {
            if (_client != null) { try { _client.Dispose(); } catch { } }
            _client = c;
        }

        // Bitta adapterni sinab ko'radi: ochib, ulanib ko'radi. Muvaffaqiyatli bo'lsa true.
        private bool TryOpen(J2534Device d)
        {
            Log("   sinalmoqda: " + d.Name + " ...");
            DelphiMt60Client c = null;
            try
            {
                c = new DelphiMt60Client(d.DllPath, Log);
                c.Connect();
                SwapClient(c);
                SaveDll(d.DllPath);   // keyingi safar shu adapter avtomatik tanlanadi
                return true;
            }
            catch (Exception ex)
            {
                if (c != null) { try { c.Dispose(); } catch { } }
                Log("      -> ulanmadi (" + ex.Message + ")");
                return false;
            }
        }

        private void NeedConn() { if (!_connected) throw new Exception("Avval ULANING (1-tugma)."); }

        // ================= Amallar =================

        private void DoConnect()
        {
            RunBg("Ulanmoqda", delegate ()
            {
                J2534Device sel = _selected;
                bool auto = (sel == null || string.IsNullOrEmpty(sel.DllPath));

                if (!auto)
                {
                    // Aniq adapter tanlangan — faqat shuni sinaymiz (boshqa drayver oynalari ochilmaydi)
                    if (!File.Exists(sel.DllPath)) throw new Exception("DLL topilmadi: " + sel.DllPath);
                    if (TryOpen(sel)) { SetConnected(true); Log("[OK] Ulandi: " + sel.Name); return; }
                    throw new Exception("Ulanmadi: " + sel.Name +
                        ". Tekshiring: adapter USB'ga va mashina OBD'ga ulanganmi? Kontakt yoqilganmi? Boshqa dastur (ChipTuningPRO/ECU Flash) adapterni band qilmaganmi?");
                }

                // Avto — tartib bilan sinaymiz (Scanmatik birinchi)
                List<J2534Device> cand = OrderedCandidates();
                if (cand.Count == 0) throw new Exception("Adapter topilmadi. 'Adapterni almashtirish' -> DLL... bilan ko'rsating.");
                Log("Avto: ulangan adapter qidirilmoqda (" + cand.Count + " ta)...");
                foreach (J2534Device d in cand)
                    if (TryOpen(d)) { SetAdapter(d); SetConnected(true); Log("[OK] Ulandi: " + d.Name); return; }
                throw new Exception("Hech bir adapter ulanmadi. Adapter va mashina ulanganmi? Kontakt yoqilganmi?");
            });
        }

        private void DoIdent()
        {
            RunBg("Ident o'qish", delegate ()
            {
                NeedConn();
                List<string[]> rows = _client.ReadPassportTable();
                Log("--- EBU PASPORTI ---");
                foreach (string[] r in rows) Log(string.Format("  {0,-26}: {1}", r[0], r[1]));
                ShowPassport(rows);
            });
        }

        private void ShowPassport(List<string[]> rows)
        {
            if (InvokeRequired) { Invoke(new Action<List<string[]>>(ShowPassport), rows); return; }
            Form f = new Form();
            f.Text = "EBU Pasporti — Delphi MT60";
            f.ClientSize = new Size(580, 420); f.StartPosition = FormStartPosition.CenterParent;
            f.BackColor = BG; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;

            ListView lv = new ListView();
            lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.GridLines = true;
            lv.BackColor = LOGBG; lv.ForeColor = TXT; lv.Font = new Font("Segoe UI", 10f); lv.BorderStyle = BorderStyle.None;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.Columns.Add("Parametr", 250); lv.Columns.Add("Qiymat", 310);
            foreach (string[] r in rows)
            {
                ListViewItem it = new ListViewItem(r[0]); it.SubItems.Add(r[1]); lv.Items.Add(it);
            }

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 50; bottom.BackColor = PANEL;
            Button save = new Button(); save.Text = "Saqlash (.txt)"; StyleBtn(save, BLUE); save.Size = new Size(140, 32); save.Location = new Point(300, 9);
            Button close = new Button(); close.Text = "Yopish"; StyleBtn(close, GRAY); close.Size = new Size(120, 32); close.Location = new Point(448, 9);
            close.Click += delegate { f.Close(); };
            save.Click += delegate
            {
                SaveFileDialog sfd = new SaveFileDialog(); sfd.Filter = "Matn|*.txt"; sfd.FileName = "pasport.txt";
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (string[] r in rows) sb.AppendLine(r[0] + ": " + r[1]);
                    File.WriteAllText(sfd.FileName, sb.ToString());
                }
            };
            bottom.Controls.Add(save); bottom.Controls.Add(close);

            Label hdr = new Label();
            hdr.Text = "EBU PASPORTI"; hdr.Dock = DockStyle.Top; hdr.Height = 36;
            hdr.ForeColor = TXT; hdr.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            hdr.TextAlign = ContentAlignment.MiddleCenter; hdr.BackColor = PANEL;

            f.Controls.Add(lv); f.Controls.Add(bottom); f.Controls.Add(hdr);
            f.ShowDialog();
        }

        private void DoIdentLog()
        {
            RunBg("Ident o'qish", delegate ()
            {
                NeedConn();
                Dictionary<string, string> p = _client.ReadEcuPassport();
                Log("--- EBU PASPORTI ---");
                if (p.Count == 0) Log("  (bo'sh — EBU holati/ulanishni tekshiring)");
                foreach (KeyValuePair<string, string> kv in p)
                    Log(string.Format("  {0,-8}: {1}", kv.Key, kv.Value));
            });
        }

        private void DoRead()
        {
            RunBg("Flash o'qish", delegate ()
            {
                NeedConn();
                Log("O'qish boshlanmoqda (bir necha daqiqa)...");
                byte[] data = _client.ReadFullFlash(OnProgress);
                Invoke(new MethodInvoker(delegate ()
                {
                    SaveFileDialog sfd = new SaveFileDialog();
                    sfd.Filter = "BIN fayl|*.bin";
                    sfd.FileName = "read_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        File.WriteAllBytes(sfd.FileName, data);
                        Log("Saqlandi: " + sfd.FileName + "  (" + data.Length + " bayt)");
                    }
                    else Log("Saqlash bekor qilindi (ma'lumot o'qildi).");
                }));
            });
        }

        private void DoWrite()
        {
            RunBg("Flash yozish", delegate ()
            {
                NeedConn();
                string fp = null;
                Invoke(new MethodInvoker(delegate ()
                {
                    OpenFileDialog ofd = new OpenFileDialog();
                    ofd.Filter = "BIN fayl|*.bin";
                    if (ofd.ShowDialog() == DialogResult.OK) fp = ofd.FileName;
                }));
                if (fp == null) { Log("Yozish bekor qilindi."); return; }

                byte[] file = File.ReadAllBytes(fp);
                if (file.Length != DelphiMt60Client.FULL_FLASH_15MB)
                {
                    Log(string.Format("DIQQAT: fayl {0} bayt, kerak {1} bayt. Bekor.",
                        file.Length, DelphiMt60Client.FULL_FLASH_15MB));
                    return;
                }
                Log("Rejim: backup=ON, verify=ON.");
                bool ok = _client.WriteFullFlash(file, OnProgress, true, true,
                    delegate (Dictionary<string, string> p)
                    {
                        bool res = false;
                        Invoke(new MethodInvoker(delegate ()
                        {
                            string msg = "EBU PASPORTI:\n\n";
                            foreach (KeyValuePair<string, string> kv in p)
                                msg += string.Format("  {0}: {1}\n", kv.Key, kv.Value);
                            msg += "\nShu EBU va fayl OILASI mos keladimi?\nYozishni tasdiqlaysizmi?";
                            res = MessageBox.Show(msg, "Yozishni tasdiqlang",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                        }));
                        return res;
                    });
                Log(ok ? ">>> YOZISH OK (verify o'tdi)." : ">>> YOZISH BEKOR/XATO — backupni tekshiring.");
            });
        }

        private void DoVin()
        {
            string vin = InputBox("VIN yozish", "Yangi VIN (17 belgi):", "");
            if (vin == null) return;
            vin = vin.Trim().ToUpper();
            if (vin.Length != 17) { Log("VIN 17 belgi bo'lishi shart."); return; }
            if (MessageBox.Show("VIN: " + vin + "\n\nYozishni tasdiqlaysizmi?", "Tasdiq",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { Log("Bekor."); return; }

            RunBg("VIN yozish", delegate ()
            {
                NeedConn();
                Log(_client.WriteVin(vin) ? ">>> VIN yozildi." : ">>> VIN yozilmadi.");
            });
        }

        // Oddiy input oynasi (Microsoft.VisualBasic'siz)
        private static string InputBox(string title, string prompt, string def)
        {
            Form f = new Form();
            f.Text = title; f.ClientSize = new Size(380, 130);
            f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
            f.MaximizeBox = false; f.MinimizeBox = false; f.BackColor = BG;
            Label l = new Label(); l.Text = prompt; l.ForeColor = TXT; l.Location = new Point(14, 14); l.AutoSize = true;
            TextBox t = new TextBox(); t.Text = def; t.Location = new Point(14, 40); t.Size = new Size(352, 24);
            t.BackColor = LOGBG; t.ForeColor = TXT; t.BorderStyle = BorderStyle.FixedSingle;
            Button ok = new Button(); ok.Text = "OK"; StyleBtn(ok, BLUE); ok.Size = new Size(90, 30);
            ok.Location = new Point(176, 80); ok.DialogResult = DialogResult.OK;
            Button cancel = new Button(); cancel.Text = "Bekor"; StyleBtn(cancel, GRAY); cancel.Size = new Size(90, 30);
            cancel.Location = new Point(276, 80); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? t.Text : null;
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
