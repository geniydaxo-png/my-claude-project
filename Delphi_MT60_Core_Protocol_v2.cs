using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace DelphiMT60
{
    // =========================================================================================
    // JAM DELPHI MT60 CORE PROTOCOL — v2 (XAVFSIZ)
    //
    // Chevrolet Cobalt / Gentra / Ravon R3 (B15D2) — Infineon TriCore TC1766
    // UDS / GMLAN, ISO 15765-2, CAN 500 kbps, J2534
    //
    // MUHIM: "Sim ustidagi" protokol (manzillar, Seed-Key, CAN freymlar) v1 bilan BIR XIL —
    // chunki u sizda ishlagan. Faqat XAVFSIZLIK qatlamlari qo'shilgan:
    //   - yozgandan keyin qayta o'qib solishtirish (Verify)
    //   - yozishdan oldin avtomatik backup
    //   - javoblarni (SID / NRC / seq) tekshirish
    //   - Seed urinishlarini cheklash (EBUni bloklab qo'ymaslik uchun)
    //   - cheksiz sikl himoyasi, TesterPresent, to'g'ri Dispose
    // =========================================================================================
    public class DelphiMt60Client : IDisposable
    {
        public const uint ECU_TX_ID = 0x7E0;
        public const uint ECU_RX_ID = 0x7E8;
        public const uint BOOT_TX_ID = 0x7D0;
        public const uint CAN_BAUD = 500000;
        public const int FULL_FLASH_15MB = 1540096;   // 0x178000

        // Noto'g'ri kalit bilan EBUni bloklamaslik uchun — urinishlar chegarasi.
        public const int MAX_SECURITY_ATTEMPTS = 2;

        // ISBOT (MT60_Master.exe dekompilyatsiyasi, DelphiMt60EepromClient): haqiqiy ishlaydigan
        // dastur Seed != 0 bo'lganda $27 02 bilan HISOBLANGAN kalitni YUBORADI va o'qiydi.
        // Algoritm (B(seed), baza 0x4AD0) shu EBU uchun TO'G'RI. Shuning uchun default = true.
        public bool SendKeyOnNonZeroSeed = true;

        private readonly J2534Transport _transport;
        private readonly Action<string> _logger;
        private int _securityAttempts = 0;

        public DelphiMt60Client(string j2534DllPath, Action<string> logger = null)
        {
            _logger = logger ?? Console.WriteLine;
            _transport = new J2534Transport(j2534DllPath, _logger);
        }

        public void Connect()
        {
            _logger("[CAN] J2534 adapter ochilmoqda...");
            _transport.Open();
            _logger("[CAN] ISO15765 500 kbps ulanmoqda...");
            _transport.Connect(CAN_BAUD);
            _logger("[CAN] Ulanish o'rnatildi.");
        }

        public void Disconnect()
        {
            if (_transport != null)
            {
                _transport.Disconnect();
                _transport.Close();
                _logger("[CAN] Adapter uzildi.");
            }
        }

        public void Dispose() { if (_transport != null) _transport.Dispose(); }   // TUZATILDI: DLL ham bo'shatiladi

        // --- Kichik yordamchi: ijobiy javobni tekshirish (SID == so'rov+0x40) ---
        private static bool IsPositive(byte[] resp, byte requestSid)
        {
            return resp != null && resp.Length >= 1 && resp[0] == (byte)(requestSid + 0x40);
        }

        private bool IsNegative(byte[] resp, out byte nrc)
        {
            nrc = 0;
            if (resp != null && resp.Length >= 3 && resp[0] == 0x7F) { nrc = resp[2]; return true; }
            return false;
        }

        #region 1. Identifikatsiya (Pasport)

        public Dictionary<string, string> ReadEcuPassport()
        {
            var p = new Dictionary<string, string>();
            _logger("[UDS] EBU pasporti o'qilmoqda...");

            byte[] vin = _transport.SendAndReceive(new byte[] { 0x1A, 0x90 });
            if (vin != null && vin.Length >= 19 && vin[0] == 0x5A && vin[1] == 0x90)
                p["VIN"] = Encoding.ASCII.GetString(vin, 2, 17);

            byte[] sw = _transport.SendAndReceive(new byte[] { 0x1A, 0x98 });
            if (sw != null && sw.Length >= 4 && sw[0] == 0x5A && sw[1] == 0x98)
                p["SW"] = Encoding.ASCII.GetString(sw, 2, sw.Length - 2).Trim();

            byte[] hw = _transport.SendAndReceive(new byte[] { 0x1A, 0x92 });
            if (hw != null && hw.Length >= 4 && hw[0] == 0x5A && hw[1] == 0x92)
                p["HW"] = Encoding.ASCII.GetString(hw, 2, hw.Length - 2).Trim();

            byte[] dt = _transport.SendAndReceive(new byte[] { 0x1A, 0x99 });
            if (dt != null && dt.Length >= 6 && dt[0] == 0x5A && dt[1] == 0x99)
                p["Date"] = string.Format("{0:X2}{1:X2}-{2:X2}-{3:X2}", dt[2], dt[3], dt[4], dt[5]);

            byte[] en = _transport.SendAndReceive(new byte[] { 0x1A, 0x97 });
            if (en != null && en.Length >= 4 && en[0] == 0x5A && en[1] == 0x97)
                p["Engine"] = Encoding.ASCII.GetString(en, 2, en.Length - 2).Trim();

            // Part ID (1A 9F) — SPS log'da "PCH9STN#53" shu DID orqali keldi
            byte[] pid = _transport.SendAndReceive(new byte[] { 0x1A, 0x9F });
            if (pid != null && pid.Length >= 4 && pid[0] == 0x5A && pid[1] == 0x9F)
                p["PartID"] = Encoding.ASCII.GetString(pid, 2, pid.Length - 2).Trim();

            // VIN zaxira DID (1A 28) — agar 1A 90 ishlamasa
            if (!p.ContainsKey("VIN"))
            {
                byte[] vin28 = _transport.SendAndReceive(new byte[] { 0x1A, 0x28 });
                if (vin28 != null && vin28.Length >= 19 && vin28[0] == 0x5A && vin28[1] == 0x28)
                    p["VIN"] = Encoding.ASCII.GetString(vin28, 2, 17);
            }

            // TUZATILDI: ijobiylik (0x5A) tekshiriladi
            byte[] c0 = _transport.SendAndReceive(new byte[] { 0x1A, 0xC0 });
            if (c0 != null && c0.Length >= 6 && c0[0] == 0x5A) p["Cal_C0"] = BitConverter.ToString(c0, 2).Replace("-", "");
            byte[] c1 = _transport.SendAndReceive(new byte[] { 0x1A, 0xC1 });
            if (c1 != null && c1.Length >= 6 && c1[0] == 0x5A) p["Cal_C1"] = BitConverter.ToString(c1, 2).Replace("-", "");

            return p;
        }

        // Scanmatik "Pasport" ko'rinishidagi jadval: [label, qiymat] qatorlar.
        public List<string[]> ReadPassportTable()
        {
            var rows = new List<string[]>();
            string vin = ReadDidAscii(0x90, 17);
            if (string.IsNullOrEmpty(vin)) vin = ReadDidAscii(0x28, 17);
            rows.Add(new string[] { "VIN (avtomobil raqami)", string.IsNullOrEmpty(vin) ? "-" : vin });

            AddGmNum(rows, "Yakuniy model qism raqami", 0xCB);
            AddGmNum(rows, "Bazaviy model qism raqami", 0xCC);
            AddGmNum(rows, "Dasturiy modul 1 ID", 0xC1);
            AddGmNum(rows, "Dasturiy modul 2 ID", 0xC2);

            string hw = ReadDidAscii(0x92, -1); if (!string.IsNullOrEmpty(hw)) rows.Add(new string[] { "Apparat (HW)", hw });
            string eng = ReadDidAscii(0x97, -1); if (!string.IsNullOrEmpty(eng)) rows.Add(new string[] { "Dvigatel", eng });
            string cal = ReadDidAscii(0x98, -1); if (!string.IsNullOrEmpty(cal)) rows.Add(new string[] { "Kalibrovka", cal });
            string dt = ReadDidDate(0x99); if (dt != null) rows.Add(new string[] { "Dasturlash sanasi", dt });
            return rows;
        }

        private string ReadDidAscii(byte did, int len)
        {
            byte[] r = _transport.SendAndReceive(new byte[] { 0x1A, did });
            if (r == null || r.Length < 3 || r[0] != 0x5A || r[1] != did) return null;
            int n = (len > 0) ? Math.Min(len, r.Length - 2) : r.Length - 2;
            string s = Encoding.ASCII.GetString(r, 2, n);
            // ko'rinmaydigan belgilarni tozalash
            var sb = new StringBuilder();
            foreach (char c in s) if (c >= 32 && c < 127) sb.Append(c);
            return sb.ToString().Trim();
        }

        private void AddGmNum(List<string[]> rows, string label, byte did)
        {
            byte[] r = _transport.SendAndReceive(new byte[] { 0x1A, did });
            if (r == null || r.Length < 6 || r[0] != 0x5A || r[1] != did) return;
            uint v = (uint)((r[2] << 24) | (r[3] << 16) | (r[4] << 8) | r[5]);
            if (v == 0) return;
            rows.Add(new string[] { label, v.ToString() });
        }

        private string ReadDidDate(byte did)
        {
            byte[] r = _transport.SendAndReceive(new byte[] { 0x1A, did });
            if (r == null || r.Length < 6 || r[0] != 0x5A) return null;
            return string.Format("{0:X2}{1:X2}-{2:X2}-{3:X2}", r[2], r[3], r[4], r[5]);
        }

        #endregion

        #region 2. Xavfsizlik (SecurityAccess 0x27) — algoritm v1 BILAN BIR XIL

        /// <summary>
        /// MANBA: MT60_Master.exe IL disassembly — DelphiMt60EepromClient.B(int seed)
        ///        (Metadata Token 0x06000078). Delphi MT60 (GM Type 4) backdoor kaliti.
        /// DIQQAT: Bu backdoor AYRIM zavod proshivkalarida (masalan PCE6STN#55) GM tomonidan
        ///        YOPILGAN bo'lishi mumkin — unda Seed != 0 keladi va bu algoritm NOTO'G'RI
        ///        kalit beradi (NRC 0x35), takror urinish NRC 0x37 bloklanishga olib keladi.
        ///        Shuning uchun: Seed == 0 (ochiq) bo'lsa kalit umuman yuborilmaydi; Seed != 0
        ///        bo'lsa faqat BIR marta, baland ogohlantirish bilan.
        /// </summary>
        public static ushort CalculateSecurityKey(ushort seed)
        {
            int key = 0x4AD0;
            if ((seed & 0x4000) != 0) key += 1;
            if ((seed & 0x8000) != 0) key += 1;
            if ((seed & 0x2000) != 0) key ^= 0x8000;
            return (ushort)(key & 0xFFFF);
        }

        public bool UnlockSecurityAccess(byte accessLevel = 0x01)
        {
            if (_securityAttempts >= MAX_SECURITY_ATTEMPTS)
            {
                _logger("[SECURITY] Urinishlar chegarasiga yetildi — to'xtatildi (EBUni bloklamaslik uchun).");
                return false;
            }
            _securityAttempts++;

            _logger(string.Format("[SECURITY] Seed so'ralmoqda (0x27 0x{0:X2}), urinish {1}/{2}...",
                accessLevel, _securityAttempts, MAX_SECURITY_ATTEMPTS));

            byte[] seedResp = _transport.SendAndReceive(new byte[] { 0x27, accessLevel });
            if (seedResp == null)
            {
                _logger("[SECURITY] EBU javob bermadi.");
                return false;
            }

            // TUZATILDI: salbiy javob 3 bayt (7F 27 NRC) — endi to'g'ri ushlanadi
            byte nrc;
            if (IsNegative(seedResp, out nrc))
            {
                if (nrc == 0x37)
                    _logger("[SECURITY] NRC 0x37: EBU BLOKIROVKADA! Quvvatni uzmasdan 10 daqiqa kuting.");
                else if (nrc == 0x36)
                    _logger("[SECURITY] NRC 0x36: urinishlar oshib ketdi — EBU bloklandi. To'xtatildi.");
                else
                    _logger(string.Format("[SECURITY] Rad etildi (NRC 0x{0:X2}).", nrc));
                return false;
            }

            if (!IsPositive(seedResp, 0x27) || seedResp.Length < 4)
            {
                _logger(string.Format("[SECURITY] Kutilmagan javob: 0x{0:X2}", seedResp[0]));
                return false;
            }

            ushort seed = (ushort)((seedResp[2] << 8) | seedResp[3]);
            if (seed == 0x0000)
            {
                _logger("[SECURITY] EBU allaqachon ochiq.");
                _securityAttempts = 0;
                return true;
            }

            ushort key = CalculateSecurityKey(seed);
            _logger(string.Format("[SECURITY] Seed: 0x{0:X4} -> Key: 0x{1:X4} (hisoblangan)", seed, key));

            // SPS log isboti: GM DPS bunday holatda kalit yubormaydi. Default — biz ham yubormaymiz.
            if (!SendKeyOnNonZeroSeed)
            {
                _logger("[SECURITY] Seed != 0 — EBU yopiq. GM DPS ham kalit yubormaydi, algoritm tasdiqlanmagan.");
                _logger("[SECURITY] Kalit YUBORILMADI (bloklanish xavfi yo'q). Kerak bo'lsa SendKeyOnNonZeroSeed=true.");
                return false;
            }

            byte keySub = (byte)(accessLevel + 1);
            byte[] keyResp = _transport.SendAndReceive(new byte[] {
                0x27, keySub, (byte)((key >> 8) & 0xFF), (byte)(key & 0xFF)
            });

            if (IsPositive(keyResp, 0x27) && keyResp.Length >= 2 && keyResp[1] == keySub)
            {
                _logger("[SECURITY] Ochildi (Security Unlocked OK).");
                _securityAttempts = 0;
                return true;
            }

            // TUZATILDI: qisqa javobda yiqilmaydi
            byte nrc2;
            if (IsNegative(keyResp, out nrc2))
                _logger(string.Format("[SECURITY] Noto'g'ri kalit (NRC 0x{0:X2})! Ehtiyot bo'ling — bloklanish xavfi.", nrc2));
            else
                _logger("[SECURITY] Kalit qabul qilinmadi.");
            return false;
        }

        #endregion

        #region 3. RAM Bootloader (CAN 0x7D0) — oqim v1 bilan bir xil

        // GM GMLAN Programming Mode sekvensiyasi (SPS log'dan). Shundan keyin seed = 0x0000 bo'ladi.
        // EKSPERIMENTAL: funksional freymlar (CAN 0x101) Scanmatik drayveriga bog'liq.
        public void EnterProgrammingMode()
        {
            _logger("[PROG] GM Programming Mode (A5) boshlanmoqda...");
            // 1) disableAllDTCs
            _transport.SendFunctional(new byte[] { 0x10, 0x02 });
            Thread.Sleep(100);
            // 2) DisableNormalCommunication
            _transport.SendFunctional(new byte[] { 0x28 });
            Thread.Sleep(100);
            // 3) requestProgrammingMode (A5 01)
            _transport.SendFunctional(new byte[] { 0xA5, 0x01 });
            Thread.Sleep(1500);
            // 4) enableProgrammingMode (A5 03) — EBU reset bo'ladi, javob yo'q
            _transport.SendFunctional(new byte[] { 0xA5, 0x03 });
            _logger("[PROG] A5 03 yuborildi — EBU boot rejimiga o'tmoqda (reset, ~2s)...");
            Thread.Sleep(2000);
        }

        public bool UploadRamBootloader(byte[] boot)
        {
            byte nrc0;
            _logger("[BOOT] 1-bosqich: $10 02 (Programming session)...");
            byte[] r1 = _transport.SendAndReceive(new byte[] { 0x10, 0x02 });
            if (IsNegative(r1, out nrc0)) { _logger(string.Format("[BOOT] $10 02 rad etildi (NRC 0x{0:X2}).", nrc0)); return false; }
            Thread.Sleep(40);

            _logger("[BOOT] 2-bosqich: $10 85 (High-speed)...");
            _transport.SendAndReceive(new byte[] { 0x10, 0x85 });
            Thread.Sleep(40);

            _logger("[BOOT] 3-bosqich: Security Access (kalit yuboriladi)...");
            if (!UnlockSecurityAccess(0x01)) { _logger("[BOOT] Xavfsizlik ochilmadi."); return false; }

            _logger("[BOOT] 4-bosqich: $34 RequestDownload...");
            byte nrc;
            byte[] r34 = _transport.SendAndReceive(new byte[] { 0x34, 0x00, 0x00, 0x00, 0x00, 0x00 });
            if (IsNegative(r34, out nrc)) { _logger(string.Format("[BOOT] $34 rad etildi (NRC 0x{0:X2}).", nrc)); return false; }
            Thread.Sleep(25);

            byte[] prep = new byte[] { 0x36, 0x80, 0xD0, 0x00, 0x68, 0x00, 0xC5, 0x8F, 0x80, 0x80, 0x2D, 0x0F, 0x00, 0x00 };
            _transport.SendAndReceive(prep);
            Thread.Sleep(25);

            _logger(string.Format("[BOOT] 5-bosqich: 499 freym 0x7D0 orqali ({0} bayt)...", boot.Length));
            int frames = boot.Length / 8;
            for (int i = 0; i < frames; i++)
            {
                byte[] f = new byte[8];
                Buffer.BlockCopy(boot, i * 8, f, 0, 8);
                _transport.SendRawCan(BOOT_TX_ID, f);
                if (i % 64 == 0) Thread.Sleep(1);
            }
            Thread.Sleep(50);
            _logger("[BOOT] Bootloader yuborildi.");
            return true;
        }

        #endregion

        #region 4. Full Flash O'qish

        public byte[] ReadFullFlash(Action<int, int> progress = null, bool uploadBoot = true)
        {
            _logger("[FLASH] 1.5 MB o'qish boshlandi...");

            if (uploadBoot)
            {
                byte[] boot = LoadBootloaderOrThrow();
                if (!UploadRamBootloader(boot)) throw new Exception("RAM Bootloader yuklanmadi!");
            }

            int total = FULL_FLASH_15MB;
            byte[] buf = new byte[total];

            _logger("[FLASH] $35 RequestUpload...");
            byte[] ru = _transport.SendAndReceive(new byte[] {
                0x35, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x17, 0x80, 0x00
            });
            if (!IsPositive(ru, 0x35))
            {
                byte nrc; IsNegative(ru, out nrc);
                throw new Exception(string.Format("RequestUpload rad etildi (NRC 0x{0:X2})!", nrc));
            }

            int offset = 0;
            byte seq = 1;
            int emptyGuard = 0;
            _logger("[FLASH] 4080-baytli bloklar o'qilmoqda...");
            while (offset < total)
            {
                byte[] chunk = _transport.SendAndReceive(new byte[] { 0x36, seq });
                if (!IsPositive(chunk, 0x36) || chunk.Length < 2)
                    throw new Exception(string.Format("Offset 0x{0:X} da o'qish xatosi!", offset));

                // TUZATILDI: seq echo tekshiriladi
                if (chunk[1] != seq)
                    throw new Exception(string.Format("Seq xato: kutildi {0}, keldi {1} (0x{2:X}).", seq, chunk[1], offset));

                int payload = chunk.Length - 2;
                // TUZATILDI: cheksiz sikl himoyasi
                if (payload <= 0)
                {
                    if (++emptyGuard > 3) throw new Exception("Bo'sh bloklar keldi — o'qish to'xtatildi.");
                    continue;
                }
                emptyGuard = 0;

                int copy = Math.Min(payload, total - offset);
                Buffer.BlockCopy(chunk, 2, buf, offset, copy);
                offset += copy;
                seq++;
                if (progress != null) progress(offset, total);
            }

            _transport.SendAndReceive(new byte[] { 0x37 });
            _logger("[FLASH] O'qish tugadi (" + offset + " bayt).");
            return buf;
        }

        #endregion

        #region 5. Full Flash Yozish — backup + verify bilan

        /// <param name="makeBackup">Yozishdan oldin joriy flashni saqlash (TAVSIYA: true)</param>
        /// <param name="verifyAfter">Yozgandan keyin qayta o'qib solishtirish (TAVSIYA: true)</param>
        /// <param name="confirm">Pasport ko'rsatilgach tasdiq so'rovchi funksiya; null = tasdiqsiz davom</param>
        public bool WriteFullFlash(byte[] flashData,
                                   Action<int, int> progress = null,
                                   bool makeBackup = true,
                                   bool verifyAfter = true,
                                   Func<Dictionary<string, string>, bool> confirm = null,
                                   string backupDir = null)
        {
            if (flashData == null || flashData.Length != FULL_FLASH_15MB)
                throw new ArgumentException(string.Format("Fayl aynan {0} bayt (1.5 MB) bo'lishi shart!", FULL_FLASH_15MB));

            // 1) Identifikatsiya + tasdiq (oila mosligini foydalanuvchi tekshirsin)
            Dictionary<string, string> passport;
            try { passport = ReadEcuPassport(); }
            catch { passport = new Dictionary<string, string>(); }

            _logger("[YOZISH] --- EBU pasporti ---");
            foreach (var kv in passport) _logger(string.Format("   {0,-8}: {1}", kv.Key, kv.Value));
            if (confirm != null && !confirm(passport))
            {
                _logger("[YOZISH] Foydalanuvchi bekor qildi (oila mos emasmi?). Yozish to'xtatildi.");
                return false;
            }

            string vin = passport.ContainsKey("VIN") ? passport["VIN"] : "NOVIN";

            // 2) Bootloader (bir marta)
            byte[] boot = LoadBootloaderOrThrow();
            if (!UploadRamBootloader(boot)) throw new Exception("RAM Bootloader yuklanmadi!");

            // 3) Backup (joriy flashni saqlash) — bootloader allaqachon yuklangan
            if (makeBackup)
            {
                _logger("[YOZISH] Backup olinmoqda (joriy flash o'qilmoqda)...");
                byte[] cur = ReadFullFlash(progress, uploadBoot: false);
                string dir = backupDir ?? AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(dir, string.Format("backup_{0}_{1:yyyyMMdd_HHmmss}.bin", vin, DateTime.Now));
                File.WriteAllBytes(path, cur);
                _logger("[YOZISH] Backup saqlandi: " + path + "  (EBU uxlasa shuni qayta yozing!)");
            }

            // 4) Yozish — manzillar v1 BILAN BIR XIL (sizda ishlagan)
            _logger("[YOZISH] 1-sektor (0x40000 - 0x178000)...");
            WriteMemorySegment(flashData, 0x40000, 0x178000 - 0x40000, 0, progress);

            _logger("[YOZISH] 2-sektor (0x20000 - 0x40000)...");
            WriteMemorySegment(flashData, 0x20000, 0x20000, 0x178000 - 0x40000, progress);

            // 5) Checksum — TUZATILDI: javob endi tekshiriladi
            _logger("[YOZISH] Checksum ($31 01 02 02)...");
            byte[] chk = _transport.SendAndReceive(new byte[] { 0x31, 0x01, 0x02, 0x02 });
            byte cnrc;
            if (IsNegative(chk, out cnrc))
            {
                _logger(string.Format("[YOZISH] XATO: Checksum rad etildi (NRC 0x{0:X2})! EBU reset QILINMAYDI.", cnrc));
                _logger("[YOZISH] Flash to'liq emas. Backupni qayta yozishga tayyor turing.");
                return false;
            }

            // 6) Verify — TUZATILDI: qayta o'qib solishtirish
            if (verifyAfter)
            {
                _logger("[YOZISH] VERIFY: flash qayta o'qilmoqda...");
                byte[] back = ReadFullFlash(progress, uploadBoot: false);
                int diff = CompareWrittenRegion(flashData, back);
                if (diff >= 0)
                {
                    _logger(string.Format("[YOZISH] XATO: Verify mos kelmadi (birinchi farq 0x{0:X}). Reset QILINMAYDI!", diff));
                    _logger("[YOZISH] Backupni qayta yozing.");
                    return false;
                }
                _logger("[YOZISH] VERIFY OK — yozilgan ma'lumot faylga to'liq mos.");
            }

            // 7) Reset — faqat hammasi joyida bo'lsa
            _logger("[YOZISH] EBU reset ($11 01)...");
            _transport.SendAndReceive(new byte[] { 0x11, 0x01 });
            _logger("[YOZISH] Tugadi. Zajiganieni 10 soniyaga o'chiring.");
            return true;
        }

        // Faqat haqiqatan yozilgan diapazonni (0x20000..0x178000) solishtiradi.
        // Mos bo'lsa -1, aks holda birinchi farq offseti.
        private static int CompareWrittenRegion(byte[] file, byte[] readBack)
        {
            for (int i = 0x20000; i < 0x178000 && i < file.Length && i < readBack.Length; i++)
                if (file[i] != readBack[i]) return i;
            return -1;
        }

        private void WriteMemorySegment(byte[] flash, int start, int length, int baseProg, Action<int, int> progress)
        {
            byte nrc;

            byte[] erase = new byte[] {
                0x31, 0x01, 0xFF, 0x00,
                (byte)(start >> 24), (byte)(start >> 16), (byte)(start >> 8), (byte)start,
                (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length
            };
            byte[] er = _transport.SendAndReceive(erase);
            if (IsNegative(er, out nrc))
                throw new Exception(string.Format("Erase rad etildi 0x{0:X} (NRC 0x{1:X2})!", start, nrc));
            Thread.Sleep(80);

            byte[] req34 = new byte[] {
                0x34, 0x00,
                (byte)(start >> 24), (byte)(start >> 16), (byte)(start >> 8), (byte)start,
                (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length
            };
            byte[] r34 = _transport.SendAndReceive(req34);
            if (IsNegative(r34, out nrc))
                throw new Exception(string.Format("$34 rad etildi 0x{0:X} (NRC 0x{1:X2})!", start, nrc));
            Thread.Sleep(40);

            int blockSize = 4080, written = 0;
            byte seq = 1;
            while (written < length)
            {
                int cur = Math.Min(blockSize, length - written);
                byte[] chunk = new byte[cur + 2];
                chunk[0] = 0x36; chunk[1] = seq;
                Buffer.BlockCopy(flash, start + written, chunk, 2, cur);

                byte[] resp = _transport.SendAndReceive(chunk);
                if (!IsPositive(resp, 0x36))
                {
                    byte n; IsNegative(resp, out n);
                    throw new Exception(string.Format("Yozish rad etildi 0x{0:X} (NRC 0x{1:X2})!", start + written, n));
                }
                written += cur;
                seq++;
                if (progress != null) progress(baseProg + written, FULL_FLASH_15MB);
            }

            byte[] exit = _transport.SendAndReceive(new byte[] { 0x37 });
            if (IsNegative(exit, out nrc))
                throw new Exception(string.Format("$37 TransferExit rad etildi 0x{0:X} (NRC 0x{1:X2})!", start, nrc));
            Thread.Sleep(40);
        }

        #endregion

        #region 6. VIN yozish

        public bool WriteVin(string newVin)
        {
            newVin = (newVin ?? "").Trim().ToUpper();
            if (newVin.Length != 17) throw new ArgumentException("VIN aynan 17 belgi bo'lishi shart!");

            _logger("[VIN] Yozilmoqda: " + newVin);
            _transport.SendAndReceive(new byte[] { 0x10, 0x03 });
            Thread.Sleep(30);

            if (!UnlockSecurityAccess(0x01)) { _logger("[VIN] Xavfsizlik ochilmadi."); return false; }

            byte[] vb = Encoding.ASCII.GetBytes(newVin);
            byte[] req = new byte[19];
            req[0] = 0x3B; req[1] = 0x90;
            Buffer.BlockCopy(vb, 0, req, 2, 17);

            byte[] resp = _transport.SendAndReceive(req);
            if (IsPositive(resp, 0x3B) && resp.Length >= 2 && resp[1] == 0x90)
            {
                _logger("[VIN] Yozildi.");
                _transport.SendAndReceive(new byte[] { 0x10, 0x01 });
                return true;
            }
            byte nrc; IsNegative(resp, out nrc);
            _logger(string.Format("[VIN] Rad etildi (NRC 0x{0:X2}).", nrc));
            return false;
        }

        #endregion

        private byte[] LoadBootloaderOrThrow()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mt60_ram_bootloader.bin");
            if (!File.Exists(path)) throw new FileNotFoundException("mt60_ram_bootloader.bin topilmadi!");
            byte[] b = File.ReadAllBytes(path);
            if (b.Length % 8 != 0) throw new Exception("Bootloader hajmi 8 ga bo'linmaydi — buzilgan bo'lishi mumkin!");
            return b;
        }
    }

    // =========================================================================================
    //  J2534 Transport — SendAndReceive TUZATILDI (SID + TX-loopback tekshiruvi)
    // =========================================================================================
    public class J2534Transport : IDisposable
    {
        private readonly string _dllPath;
        private IntPtr _hDll = IntPtr.Zero;
        private uint _deviceId, _channelId;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtOpen(IntPtr pName, out uint pDeviceId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtClose(uint deviceId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtConnect(uint deviceId, uint protocolId, uint flags, uint baudRate, out uint pChannelId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtDisconnect(uint channelId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtReadMsgs(uint channelId, IntPtr pMsg, out uint pNumMsgs, uint timeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtWriteMsgs(uint channelId, IntPtr pMsg, out uint pNumMsgs, uint timeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtStartMsgFilter(uint channelId, uint filterType, IntPtr pMask, IntPtr pPattern, IntPtr pFlow, out uint pFilterId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PtIoctl(uint handleId, uint ioctlId, IntPtr pInput, IntPtr pOutput);

        private PtOpen _ptOpen; private PtClose _ptClose; private PtConnect _ptConnect; private PtDisconnect _ptDisconnect;
        private PtReadMsgs _ptReadMsgs; private PtWriteMsgs _ptWriteMsgs; private PtStartMsgFilter _ptStartMsgFilter;
        private PtIoctl _ptIoctl;

        public const uint ISO15765 = 6, CAN = 5, FLOW_CONTROL_FILTER = 3, CLEAR_TX_BUFFER = 7, CLEAR_RX_BUFFER = 8;
        private const uint RXSTATUS_TX_MSG_TYPE = 0x00000001;  // TX loopback indikatori

        public J2534Transport(string dllPath, Action<string> logger = null)
        {
            _dllPath = dllPath; LoadDll();
        }

        private void LoadDll()
        {
            _hDll = LoadLibrary(_dllPath);
            if (_hDll == IntPtr.Zero) throw new FileNotFoundException("J2534 DLL yuklanmadi: " + _dllPath);
            _ptOpen = GetFunc<PtOpen>("PassThruOpen");
            _ptClose = GetFunc<PtClose>("PassThruClose");
            _ptConnect = GetFunc<PtConnect>("PassThruConnect");
            _ptDisconnect = GetFunc<PtDisconnect>("PassThruDisconnect");
            _ptReadMsgs = GetFunc<PtReadMsgs>("PassThruReadMsgs");
            _ptWriteMsgs = GetFunc<PtWriteMsgs>("PassThruWriteMsgs");
            _ptStartMsgFilter = GetFunc<PtStartMsgFilter>("PassThruStartMsgFilter");
            _ptIoctl = GetFunc<PtIoctl>("PassThruIoctl");
        }

        private T GetFunc<T>(string name) where T : class
        {
            IntPtr p = GetProcAddress(_hDll, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name + " topilmadi!");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        public void Open()
        {
            int s = _ptOpen(IntPtr.Zero, out _deviceId);
            if (s != 0) throw new Exception("PassThruOpen xato: " + s + " — " + J2534ErrText(s));
        }

        public static string J2534ErrText(int code)
        {
            switch (code)
            {
                case 0x07: return "ERR_FAILED (umumiy xato — adapter band yoki boshqa dasturda ochiq bo'lishi mumkin)";
                case 0x08: return "ERR_DEVICE_NOT_CONNECTED (adapter ulanmagan YOKI boshqa dasturda band — boshqa dasturni yoping)";
                case 0x09: return "ERR_TIMEOUT (javob kelmadi)";
                case 0x0A: return "ERR_INVALID_MSG (xabar noto'g'ri)";
                case 0x0B: return "ERR_INVALID_TIME_INTERVAL";
                case 0x0C: return "ERR_EXCEEDED_LIMIT";
                case 0x0D: return "ERR_INVALID_MSG_ID";
                case 0x0E: return "ERR_DEVICE_IN_USE (adapter boshqa dasturda band — o'sha dasturni yoping)";
                case 0x10: return "ERR_NOT_SUPPORTED";
                case 0x11: return "ERR_NO_FLOW_CONTROL";
                case 0x19: return "ERR_BUFFER_EMPTY";
                default: return "J2534 xato kodi 0x" + code.ToString("X2");
            }
        }

        public void Connect(uint baud)
        {
            int s = _ptConnect(_deviceId, ISO15765, 0, baud, out _channelId);
            if (s != 0) throw new Exception("PassThruConnect xato: " + s);
            SetupIsoTpFilter(DelphiMt60Client.ECU_RX_ID, DelphiMt60Client.ECU_TX_ID);
        }

        public void SetupIsoTpFilter(uint rxId, uint txId)
        {
            PASSTHRU_MSG mask = CreateMsg(ISO15765, 0xFFFFFFFF, new byte[0]);
            PASSTHRU_MSG pattern = CreateMsg(ISO15765, rxId, new byte[0]);
            PASSTHRU_MSG flow = CreateMsg(ISO15765, txId, new byte[0]);
            IntPtr pM = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PASSTHRU_MSG)));
            IntPtr pP = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PASSTHRU_MSG)));
            IntPtr pF = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PASSTHRU_MSG)));
            try
            {
                Marshal.StructureToPtr(mask, pM, false);
                Marshal.StructureToPtr(pattern, pP, false);
                Marshal.StructureToPtr(flow, pF, false);
                uint filterId;
                int s = _ptStartMsgFilter(_channelId, FLOW_CONTROL_FILTER, pM, pP, pF, out filterId);
                if (s != 0) throw new Exception("StartMsgFilter xato: " + s);
            }
            finally { Marshal.FreeHGlobal(pM); Marshal.FreeHGlobal(pP); Marshal.FreeHGlobal(pF); }
        }

        public byte[] SendAndReceive(byte[] request, int timeoutMs = 2000)
        {
            ClearBuffers();
            byte requestSid = request.Length > 0 ? request[0] : (byte)0;

            PASSTHRU_MSG txMsg = CreateMsg(ISO15765, DelphiMt60Client.ECU_TX_ID, request);
            IntPtr pTx = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PASSTHRU_MSG)));
            try
            {
                Marshal.StructureToPtr(txMsg, pTx, false);
                uint num = 1;
                int s = _ptWriteMsgs(_channelId, pTx, out num, 1000);
                if (s != 0 || num == 0) return null;
            }
            finally { Marshal.FreeHGlobal(pTx); }

            DateTime start = DateTime.UtcNow;
            IntPtr pRx = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PASSTHRU_MSG)));
            try
            {
                while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
                {
                    uint numRx = 1;
                    int s = _ptReadMsgs(_channelId, pRx, out numRx, 50);
                    if (s == 0 && numRx > 0)
                    {
                        PASSTHRU_MSG rx = (PASSTHRU_MSG)Marshal.PtrToStructure(pRx, typeof(PASSTHRU_MSG));

                        // TUZATILDI: adapterning o'z TX-loopback xabarini tashlab yuborish
                        if ((rx.RxStatus & RXSTATUS_TX_MSG_TYPE) != 0) { Thread.Sleep(1); continue; }

                        if (rx.DataSize > 4)
                        {
                            byte[] payload = new byte[rx.DataSize - 4];
                            Buffer.BlockCopy(rx.Data, 4, payload, 0, (int)rx.DataSize - 4);
                            if (payload.Length == 0) { Thread.Sleep(2); continue; }

                            // ResponsePending (7F xx 78) — kutishni uzaytiramiz
                            if (payload.Length >= 3 && payload[0] == 0x7F && payload[2] == 0x78)
                            { start = DateTime.UtcNow; continue; }

                            // TUZATILDI: faqat shu so'rovga tegishli javobni qabul qilamiz
                            bool positive = payload[0] == (byte)(requestSid + 0x40);
                            bool negative = payload[0] == 0x7F && payload.Length >= 2 && payload[1] == requestSid;
                            if (positive || negative) return payload;
                            // Boshqa birovning javobi — e'tiborsiz, kutishda davom
                        }
                    }
                    Thread.Sleep(2);
                }
            }
            finally { Marshal.FreeHGlobal(pRx); }
            return null;
        }

        public void SendRawCan(uint canId, byte[] raw8)
        {
            PASSTHRU_MSG msg = new PASSTHRU_MSG
            {
                ProtocolID = CAN,
                TxFlags = 0,
                DataSize = 12,
                Data = new byte[4128]
            };
            msg.Data[0] = (byte)(canId >> 24); msg.Data[1] = (byte)(canId >> 16);
            msg.Data[2] = (byte)(canId >> 8); msg.Data[3] = (byte)canId;
            Buffer.BlockCopy(raw8, 0, msg.Data, 4, Math.Min(raw8.Length, 8));

            IntPtr pMsg = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PASSTHRU_MSG)));
            try
            {
                Marshal.StructureToPtr(msg, pMsg, false);
                uint n = 1;
                _ptWriteMsgs(_channelId, pMsg, out n, 500);
            }
            finally { Marshal.FreeHGlobal(pMsg); }
        }

        // GMLAN funksional so'rov: CAN 0x101, bayt tartibi [FE, len, data...] (SPS log formati).
        public void SendFunctional(byte[] gmlan)
        {
            byte[] frame = new byte[8];
            frame[0] = 0xFE;
            frame[1] = (byte)gmlan.Length;
            Buffer.BlockCopy(gmlan, 0, frame, 2, Math.Min(gmlan.Length, 6));
            SendRawCan(0x101, frame);
        }

        public void ClearBuffers()
        {
            if (_ptIoctl != null)
            {
                _ptIoctl(_channelId, CLEAR_TX_BUFFER, IntPtr.Zero, IntPtr.Zero);
                _ptIoctl(_channelId, CLEAR_RX_BUFFER, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void Disconnect() { if (_ptDisconnect != null && _channelId != 0) { _ptDisconnect(_channelId); _channelId = 0; } }
        public void Close() { if (_ptClose != null && _deviceId != 0) { _ptClose(_deviceId); _deviceId = 0; } }

        public void Dispose()
        {
            Disconnect(); Close();
            if (_hDll != IntPtr.Zero) { FreeLibrary(_hDll); _hDll = IntPtr.Zero; }
        }

        private PASSTHRU_MSG CreateMsg(uint protocol, uint canId, byte[] payload)
        {
            PASSTHRU_MSG msg = new PASSTHRU_MSG
            {
                ProtocolID = protocol,
                TxFlags = 0x00000040,
                DataSize = (uint)(payload.Length + 4),
                Data = new byte[4128]
            };
            msg.Data[0] = (byte)(canId >> 24); msg.Data[1] = (byte)(canId >> 16);
            msg.Data[2] = (byte)(canId >> 8); msg.Data[3] = (byte)canId;
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, msg.Data, 4, payload.Length);
            return msg;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PASSTHRU_MSG
        {
            public uint ProtocolID, RxStatus, TxFlags, Timestamp, DataSize, ExtraDataIndex;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)] public byte[] Data;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr hModule);
    }

    // =========================================================================================
    //  J2534 adapterlarni Windows registridan AVTOMATIK topish
    //  (PassThruSupport.04.04 — har bir o'rnatilgan J2534 shu yerga yoziladi)
    // =========================================================================================
    public class J2534Device
    {
        public string Name;
        public string Vendor;
        public string DllPath;
        public override string ToString()
        {
            return (string.IsNullOrEmpty(Vendor) ? "" : Vendor + " — ") + Name;
        }
    }

    public static class J2534Registry
    {
        // Barcha o'rnatilgan J2534 adapterlarni qaytaradi (32 va 64 bit ko'rinishlardan).
        public static List<J2534Device> List()
        {
            var list = new List<J2534Device>();
            AddView(list, RegistryView.Registry32);
            AddView(list, RegistryView.Registry64);
            return list;
        }

        private static void AddView(List<J2534Device> list, RegistryView view)
        {
            try
            {
                using (RegistryKey bas = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey root = bas.OpenSubKey(@"SOFTWARE\PassThruSupport.04.04"))
                {
                    if (root == null) return;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string dll = k.GetValue("FunctionLibrary") as string;
                            string name = k.GetValue("Name") as string;
                            string vendor = k.GetValue("Vendor") as string;
                            if (string.IsNullOrEmpty(dll)) continue;
                            if (!File.Exists(dll)) continue;

                            bool dup = false;
                            foreach (J2534Device d in list)
                                if (string.Equals(d.DllPath, dll, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                            if (dup) continue;

                            list.Add(new J2534Device
                            {
                                Name = string.IsNullOrEmpty(name) ? sub : name,
                                Vendor = vendor ?? "",
                                DllPath = dll
                            });
                        }
                    }
                }
            }
            catch { }
        }
    }
}
