# DELPHI MT60 — TAHLIL VA TUZATISH BLOKNOTI

Avtomobil: Chevrolet Cobalt / Gentra / Ravon R3 (B15D2)
Kontroller: Infineon TriCore SAK-TC1766
Protokol: UDS / GMLAN, ISO 15765-2, CAN 500 kbps, J2534

Bu hujjat: nimani hisobladim, nima xato edi, nimasi to'g'ri edi, nima qildim.

---

## 1. NIMANI HISOBLADIM / TEKSHIRDIM (fakt)

| Narsa | Natija |
|---|---|
| `mt60_ram_bootloader.bin` hajmi | 3992 bayt = aynan **499 ta** 8-baytli freym (8 ga bo'linadi) — kod bilan mos |
| `Delphi_MT60_Core_Protocol.exe` | `.cs` dan yig'ilgan, ichidagi matnlar bir xil — bu haqiqiy kod |
| `MT60_Master_PRO_v10.exe` | Bu faqat **qobiq (launcher)**. U yonidagi `MT60_Master.exe` ni yuklaydi. Haqiqiy aloqa kodi protokol faylida |
| Seed-Key algoritmi | Seedning faqat **3 ta biti**ga bog'liq → jami bor-yo'g'i **8 xil kalit** chiqadi. Haqiqiy algoritmga o'xshamaydi, lekin sizda ishlagani uchun TEGMADIM |

### "Oila almashganda EBU uxladi" — sababi
MT60 da 15 pasport, 3 oila bor. Boshqa oiladan olingan faylni yozsangiz:
- checksum yoki kalibratsiya mos kelmaydi → EBU yuklanmaydi ("uxlaydi"),
- yoki noto'g'ri kalit urinishlari EBUni bloklaydi (NRC 0x36/0x37).

**Yagona ishonchli himoya:** yozishdan oldin EBUdan o'qib **zaxira (backup)** saqlash, va faqat **shu oilaga mos** faylni yozish. EBU uxlab qolsa — saqlangan backupni qayta yozib tiriltirish mumkin. Yangi dastur buni avtomatik qiladi.

---

## 2. NIMADA XATO EDI (asl koddagi kamchiliklar)

### 🔴 Kritik (EBUni o'ldirishi mumkin)
1. **Yozish natijasi tekshirilmaydi.** `chkResp` (checksum javobi) o'qiladi-yu, ishlatilmaydi. Funksiya doim `true` qaytaradi va "100% yozildi" deydi. Erase/$34/$37 javoblari ham tashlab yuboriladi.
2. **Yozilgandan keyin qayta o'qib solishtirilmaydi (verify yo'q).** Yarim yozilgan flash ham "muvaffaqiyatli" deb ko'rsatiladi.
3. **Yozishdan oldin backup olinmaydi.** EBU uxlab qolsa, tiriltirish uchun hech narsa qolmaydi.
4. **Bootloader bosqich javoblari tekshirilmaydi** ($10 02, $10 85, $34). EBU dasturlash rejimiga o'tmasa ham kod davom etadi.

### 🟠 Jiddiy
5. **NRC 0x37 (blokirovka) hech qachon aniqlanmaydi.** Salbiy javob 3 bayt (`7F 27 37`), lekin kod `Length < 4` ni tekshiradi → blokirovka "javob bermadi" deb ko'rsatiladi.
6. **O'qishda cheksiz sikl xavfi.** EBU bo'sh `76 xx` qaytarsa `payloadLen = 0` → `offset` oshmaydi → dastur abadiy osiladi.
7. **Har qanday javob "o'zimniki" deb olinadi.** Javob SID'i (so'rov+0x40) va adapterning TX-loopback (`RxStatus`) teshirilmaydi.
8. **Ketma-ketlik raqami (seq) tekshirilmaydi** — bloklar o'rni almashib ketishi mumkin.

### 🟡 Kichik
9. `keyResp[2]` — javob qisqa bo'lsa dastur yiqiladi (IndexOutOfRange).
10. `Dispose` transportni yopmaydi → DLL xotirada qoladi.
11. Uzoq amalda TesterPresent ($3E) yuborilmaydi → seans tugab qolishi mumkin.
12. Progress jami 0x158000 yoziladi, lekin 0x178000 ga bo'linadi → 100% ga yetmaydi.

---

## 3. NIMASI TO'G'RI EDI (tegmadim)

Bular sizda ISHLAGANI uchun ataylab o'zgartirmadim — "standart UDS" ga ko'ra tuzatsam, EBU o'lishi mumkin edi:

- **Seed-Key algoritmi** — o'zingiznikicha qoldirdim.
- **Manzillar:** o'qish 0x80000000 (absolyut), yozish 0x20000/0x40000 (nisbiy). Ikkisi har xil, lekin ikkalasi ham sizda ishlagan — demak bootloader shunday kutadi. TEGMADIM.
- **$34/$35 formatida `0x44` (addressAndLengthFormatIdentifier) yo'qligi** — bu maxsus bootloader, standart UDS emas. TEGMADIM.
- **Bootloader freymlari `ProtocolID = CAN` bilan** ISO15765 kanalida — g'alati, lekin sizning drayveringiz qabul qilgan. TEGMADIM.
- Blok o'lchami 4080 bayt, CAN ID (7E0/7E8/7D0), 500 kbps — hammasi joyida.

---

## 4. NIMA QILDIM (v2 dasturda)

Faqat XAVFSIZLIK qo'shdim, ishlaydigan protokolni buzmadim:

1. **Verify (qayta o'qib solishtirish)** — yozgandan keyin flash qayta o'qiladi va bayt-bayt tekshiriladi. Mos kelmasa — OGOHLANTIRISH va `false`.
2. **Avtomatik backup** — yozishdan oldin joriy flash `backup_<VIN>_<sana>.bin` ga saqlanadi.
3. **Checksum/Erase/$34/$37 javoblari endi tekshiriladi** — xato bo'lsa amal to'xtaydi (davom etmaydi = xavfsiz).
4. **Seed urinishlari cheklandi** — noto'g'ri kalit bilan EBUni bloklab qo'ymaslik uchun. NRC 0x36/0x37 ushlanadi va darhol to'xtatiladi.
5. **Cheksiz sikl himoyasi** — bo'sh blok yoki seq xatosida to'xtaydi.
6. **Javob validatsiyasi** — SID mosligi + TX-loopback (`RxStatus`) tashlab yuboriladi.
7. **Identifikatsiya tekshiruvi** — yozishdan oldin EBU pasporti (VIN/HW/SW) o'qiladi va ko'rsatiladi; foydalanuvchi tasdig'isiz yozilmaydi.
8. **TesterPresent ($3E)** — uzoq amalda fonda yuboriladi.
9. **Dispose, progress, qisqa-javob yiqilishi** tuzatildi.

**Tezlik:** ortiqcha `Thread.Sleep` lar kamaytirildi va so'rovlar oralig'i tejaldi. Lekin asosiy tezlik EBU va CANga bog'liq (4080-baytli blok allaqachon maksimal). Verify yoqilsa, yozish ~2 barobar uzoqroq (qayta o'qish) — lekin xavfsizlik uchun shart. Verifyni o'chirib bo'ladi, LEKIN tavsiya etilmaydi.

---

## 5. HALI HAM EHTIYOT BO'LING (men hal qila olmaganim)

- **Seed-Key'ni o'ylab topmadim.** Agar u ba'zi oilalarda noto'g'ri bo'lsa — bloklanish bo'ladi. Shuning uchun urinishlar cheklangan.
- **"Oila" mosligini avtomatik aniqlay olmayman** — fayldagi HW/SW qaysi offsetda turishini bilmayman. Shuning uchun: faqat SHU EBUdan o'qilgan yoki shu oilaga aniq mos faylni yozing.
- **Birinchi qoida:** backupni hech qachon o'chirmang. EBU uxlasa — backupni qayta yozing.
```
