/**
 * ============================================================================
 * YOMIC V2 - GOOGLE SPREADSHEET FEEDBACK RECEIVER (Hardened)
 * ============================================================================
 * Akun Google Drive Tujuan : info.yomic@gmail.com
 * Nama File Spreadsheet    : Yomic - Masukan Pengguna
 * Catatan Privasi          : Screenshot disimpan secara PRIVAT di Google Drive pemilik (tidak dibagikan publik).
 * ============================================================================
 */

const TARGET_OWNER_EMAIL = 'info.yomic@gmail.com';
const SPREADSHEET_NAME = 'Yomic - Masukan Pengguna';
const SHEET_NAME = 'Masukan';
const SCREENSHOT_FOLDER_NAME = 'Yomic - Screenshot Masukan';

const MAX_MESSAGE_LENGTH = 10000;
const MAX_SCREENSHOT_BYTES = 6 * 1024 * 1024; // 6 MB decoded cap
const ALLOWED_IMAGE_MIMES = {
  'image/png': '.png',
  'image/jpeg': '.jpg',
  'image/jpg': '.jpg',
  'image/webp': '.webp',
  'image/gif': '.gif'
};

// Opsional: Jika dibuka dari script.google.com biasa (bukan dari menu Ekstensi Spreadsheet),
// Anda juga bisa menempelkan ID Spreadsheet dari URL Google Sheets Anda di sini:
const CUSTOM_SPREADSHEET_ID = '';

/**
 * Mencegah Spreadsheet Formula Injection (=, +, -, @, tab, CR) saat membuka Google Sheets.
 */
function sanitizeSpreadsheetCell_(value, maxLen) {
  let str = String(value == null ? '' : value).trim();
  if (maxLen && str.length > maxLen) {
    str = str.substring(0, maxLen);
  }
  if (/^[=+\-@\t\r]/.test(str)) {
    return "'" + str;
  }
  return str;
}

const RATE_LIMIT_PER_CLIENT_PER_HOUR = 3;
const RATE_LIMIT_GLOBAL_PER_HOUR = 30;
const RATE_LIMIT_SCREENSHOTS_PER_HOUR = 15;

function checkAndIncrementRateLimit_(clientKey, hasScreenshot) {
  const cache = CacheService.getScriptCache();
  // Fixed hourly bucket prevents sliding-window TTL lockout under normal steady traffic
  const hourBucket = String(Math.floor(Date.now() / 3600000));
  const safeKey = String(clientKey || 'anon').replace(/[^a-zA-Z0-9_-]/g, '').substring(0, 40) || 'anon';
  const clientCacheKey = 'yomic_rl_c_' + hourBucket + '_' + safeKey;
  const globalCacheKey = 'yomic_rl_g_' + hourBucket;
  const shotCacheKey = 'yomic_rl_s_' + hourBucket;

  const globalCount = parseInt(cache.get(globalCacheKey) || '0', 10) || 0;
  if (globalCount >= RATE_LIMIT_GLOBAL_PER_HOUR) {
    return 'Layanan masukan sedang sibuk. Silakan coba lagi beberapa saat lagi.';
  }

  const clientCount = parseInt(cache.get(clientCacheKey) || '0', 10) || 0;
  if (clientCount >= RATE_LIMIT_PER_CLIENT_PER_HOUR) {
    return 'Batas pengiriman masukan tercapai (maksimal 3 per jam). Silakan coba lagi nanti.';
  }

  if (hasScreenshot) {
    const shotCount = parseInt(cache.get(shotCacheKey) || '0', 10) || 0;
    if (shotCount >= RATE_LIMIT_SCREENSHOTS_PER_HOUR) {
      return 'Batas unggah lampiran sementara tercapai. Silakan kirim masukan tanpa lampiran atau coba lagi nanti.';
    }
    cache.put(shotCacheKey, String(shotCount + 1), 3700);
  }

  cache.put(globalCacheKey, String(globalCount + 1), 3700);
  cache.put(clientCacheKey, String(clientCount + 1), 3700);
  return null;
}

/**
 * Menangani pengiriman masukan (HTTP POST) dari aplikasi desktop Yomic v2.
 */
function doPost(e) {
  const lock = LockService.getScriptLock();
  let lockAcquired = false;

  try {
    const rawBody = (e && e.postData && e.postData.contents) ? e.postData.contents : '{}';
    if (rawBody.length > 9 * 1024 * 1024) {
      return createJsonResponse_({
        ok: false,
        error: 'Ukuran permintaan terlalu besar.'
      });
    }

    const data = JSON.parse(rawBody);
    if (!data || typeof data !== 'object' || Array.isArray(data)) {
      return createJsonResponse_({
        ok: false,
        error: 'Format payload tidak valid.'
      });
    }

    const message = String(data.message || '').trim();
    if (!message) {
      return createJsonResponse_({
        ok: false,
        error: 'Deskripsi masukan tidak boleh kosong.'
      });
    }
    if (message.length > MAX_MESSAGE_LENGTH) {
      return createJsonResponse_({
        ok: false,
        error: 'Deskripsi masukan terlalu panjang (maksimal 10.000 karakter).'
      });
    }

    const categoryCode = sanitizeSpreadsheetCell_(data.category || 'bug', 40);
    const categoryLabel = sanitizeSpreadsheetCell_(data.categoryLabel || mapCategoryLabel_(categoryCode), 80);
    const appVersion = sanitizeSpreadsheetCell_(data.appVersion || '2.6.1', 32);
    const osVersion = sanitizeSpreadsheetCell_(data.osVersion || 'Windows', 120);
    const language = sanitizeSpreadsheetCell_(data.language || 'id', 16);
    const clientId = String(data.clientId || '').trim();
    const safeMessage = sanitizeSpreadsheetCell_(message, MAX_MESSAGE_LENGTH);
    const screenshotBase64 = String(data.screenshotBase64 || '').trim();
    const screenshotName = String(data.screenshotName || 'screenshot.png').trim();
    const screenshotMimeType = String(data.screenshotMimeType || 'image/png').trim().toLowerCase();

    // Cek & increment rate limit secara atomik di bawah brief script lock (mencegah burst TOCTOU)
    lock.waitLock(10000);
    lockAcquired = true;
    const rateLimitErr = checkAndIncrementRateLimit_(clientId, Boolean(screenshotBase64));
    lock.releaseLock();
    lockAcquired = false;
    if (rateLimitErr) {
      return createJsonResponse_({ ok: false, error: rateLimitErr });
    }

    // Unggah lampiran ke Drive SEBELUM mengambil lock tulis Spreadsheet agar tidak memblokir request lain
    let screenshotCellValue = '-';
    if (screenshotBase64) {
      screenshotCellValue = saveScreenshotToDrive_(screenshotBase64, screenshotName, screenshotMimeType);
    }

    lock.waitLock(15000);
    lockAcquired = true;

    const sheetInfo = getOrCreateFeedbackSheet_();
    const sheet = sheetInfo.sheet;
    const timestamp = Utilities.formatDate(new Date(), 'Asia/Jakarta', 'yyyy-MM-dd HH:mm:ss');

    sheet.appendRow([
      timestamp,
      categoryLabel,
      categoryCode,
      safeMessage,
      appVersion,
      osVersion,
      language,
      sanitizeSpreadsheetCell_(screenshotCellValue, 500)
    ]);

    return createJsonResponse_({
      ok: true,
      timestamp: timestamp
    });
  } catch (err) {
    Logger.log('Feedback doPost error: ' + String(err && err.message ? err.message : err));
    return createJsonResponse_({
      ok: false,
      error: 'Gagal memproses masukan saat ini. Silakan coba lagi nanti.'
    });
  } finally {
    if (lockAcquired) {
      lock.releaseLock();
    }
  }
}

/**
 * Menangani pengecekan status endpoint (HTTP GET) tanpa membocorkan ID/URL Spreadsheet privat.
 */
function doGet() {
  return createJsonResponse_({
    ok: true,
    service: 'Yomic v2 Feedback Service'
  });
}

/**
 * Fungsi inisialisasi manual untuk membuat tabel header biru di Spreadsheet.
 */
function setupSpreadsheet() {
  const info = getOrCreateFeedbackSheet_();
  Logger.log('Spreadsheet berhasil disiapkan untuk: ' + TARGET_OWNER_EMAIL);
  Logger.log('URL Spreadsheet: ' + info.spreadsheetUrl);
}

/**
 * Membuka Spreadsheet aktif (atau membuat baru untuk akun pemilik).
 * Catatan: DriveApp digunakan terpisah hanya saat menyimpan lampiran screenshot privat.
 */
function getOrCreateFeedbackSheet_() {
  const props = PropertiesService.getScriptProperties();
  let spreadsheet = null;

  // 1. Jika script dibuka langsung dari dalam Google Spreadsheet (Ekstensi -> Apps Script)
  try {
    spreadsheet = SpreadsheetApp.getActiveSpreadsheet();
  } catch (_) {
    spreadsheet = null;
  }

  // 2. Cek jika CUSTOM_SPREADSHEET_ID diisi manual
  if (!spreadsheet && CUSTOM_SPREADSHEET_ID) {
    try {
      spreadsheet = SpreadsheetApp.openById(CUSTOM_SPREADSHEET_ID);
    } catch (_) {
      spreadsheet = null;
    }
  }

  // 3. Cek dari ScriptProperties yang tersimpan sebelumnya
  if (!spreadsheet) {
    const savedId = props.getProperty('YOMIC_FEEDBACK_SPREADSHEET_ID');
    if (savedId) {
      try {
        spreadsheet = SpreadsheetApp.openById(savedId);
      } catch (_) {
        spreadsheet = null;
      }
    }
  }

  // 4. Buat Spreadsheet baru di Google Drive info.yomic@gmail.com menggunakan SpreadsheetApp saja
  if (!spreadsheet) {
    spreadsheet = SpreadsheetApp.create(SPREADSHEET_NAME);
    props.setProperty('YOMIC_FEEDBACK_SPREADSHEET_ID', spreadsheet.getId());
  }

  let sheet = spreadsheet.getSheetByName(SHEET_NAME);
  if (!sheet) {
    sheet = spreadsheet.getSheets()[0];
    if (sheet) {
      sheet.setName(SHEET_NAME);
    } else {
      sheet = spreadsheet.insertSheet(SHEET_NAME);
    }
  }

  if (sheet.getLastRow() === 0) {
    const headers = [
      'Waktu (WIB)',
      'Jenis Masukan',
      'Kode Kategori',
      'Deskripsi Masukan',
      'Versi Aplikasi',
      'Sistem Operasi',
      'Bahasa UI',
      'Tangkapan Layar'
    ];
    sheet.appendRow(headers);

    const headerRange = sheet.getRange(1, 1, 1, headers.length);
    headerRange.setBackground('#0066CC');
    headerRange.setFontColor('#FFFFFF');
    headerRange.setFontWeight('bold');
    sheet.setFrozenRows(1);

    sheet.setColumnWidth(1, 160);
    sheet.setColumnWidth(2, 200);
    sheet.setColumnWidth(3, 120);
    sheet.setColumnWidth(4, 480);
    sheet.setColumnWidth(5, 110);
    sheet.setColumnWidth(6, 220);
    sheet.setColumnWidth(7, 90);
    sheet.setColumnWidth(8, 320);
  } else if (sheet.getLastColumn() < 8) {
    const col8Header = sheet.getRange(1, 8);
    col8Header.setValue('Tangkapan Layar');
    col8Header.setBackground('#0066CC');
    col8Header.setFontColor('#FFFFFF');
    col8Header.setFontWeight('bold');
    sheet.setColumnWidth(8, 320);
  }

  return {
    spreadsheetId: spreadsheet.getId(),
    spreadsheetUrl: spreadsheet.getUrl(),
    sheet: sheet
  };
}

function hasMatchingImageMagicBytes_(bytes, mimeType) {
  if (!bytes || bytes.length < 12) return false;
  const b0 = bytes[0] & 0xFF;
  const b1 = bytes[1] & 0xFF;
  const b2 = bytes[2] & 0xFF;
  const b3 = bytes[3] & 0xFF;
  switch (mimeType) {
    case 'image/png':
      return b0 === 0x89 && b1 === 0x50 && b2 === 0x4E && b3 === 0x47;
    case 'image/jpeg':
    case 'image/jpg':
      return b0 === 0xFF && b1 === 0xD8 && b2 === 0xFF;
    case 'image/gif':
      return b0 === 0x47 && b1 === 0x49 && b2 === 0x46 && b3 === 0x38;
    case 'image/webp':
      return b0 === 0x52 && b1 === 0x49 && b2 === 0x46 && b3 === 0x46 &&
             (bytes[8] & 0xFF) === 0x57 && (bytes[9] & 0xFF) === 0x45 &&
             (bytes[10] & 0xFF) === 0x42 && (bytes[11] & 0xFF) === 0x50;
    default:
      return false;
  }
}

function saveScreenshotToDrive_(dataUrlOrBase64, fileName, mimeType) {
  try {
    let base64Data = dataUrlOrBase64;
    let detectedMime = (mimeType || 'image/png').toLowerCase();
    const match = /^data:([^;]+);base64,([\s\S]+)$/i.exec(dataUrlOrBase64);
    if (match) {
      detectedMime = match[1].trim().toLowerCase();
      base64Data = match[2];
    }

    if (!ALLOWED_IMAGE_MIMES[detectedMime]) {
      return 'Lampiran ditolak (format tidak didukung)';
    }

    const cleanBase64 = base64Data.replace(/\s+/g, '');
    if (cleanBase64.length > Math.ceil((MAX_SCREENSHOT_BYTES * 4) / 3) + 16) {
      return 'Lampiran ditolak (melebihi batas 6 MB)';
    }

    const bytes = Utilities.base64Decode(cleanBase64);
    if (!bytes || bytes.length === 0 || bytes.length > MAX_SCREENSHOT_BYTES) {
      return 'Lampiran ditolak (ukuran tidak valid)';
    }
    if (!hasMatchingImageMagicBytes_(bytes, detectedMime)) {
      return 'Lampiran ditolak (header gambar tidak cocok dengan tipe MIME)';
    }

    const allowedExt = ALLOWED_IMAGE_MIMES[detectedMime];
    const safeStem = (fileName || 'screenshot').replace(/\.[^.]+$/, '').replace(/[^\w\-]+/g, '_').substring(0, 64) || 'screenshot';
    const stampedName = Utilities.formatDate(new Date(), 'Asia/Jakarta', 'yyyyMMdd_HHmmss') + '_' + safeStem + allowedExt;
    const blob = Utilities.newBlob(bytes, detectedMime, stampedName);

    const folders = DriveApp.getFoldersByName(SCREENSHOT_FOLDER_NAME);
    let folder = null;
    while (folders.hasNext()) {
      const candidate = folders.next();
      if (!candidate.isTrashed()) {
        folder = candidate;
        break;
      }
    }
    if (!folder) {
      folder = DriveApp.createFolder(SCREENSHOT_FOLDER_NAME);
    }

    // Simpan secara PRIVAT di Drive pemilik — JANGAN setSharing(ANYONE_WITH_LINK) agar privasi user terjaga
    const file = folder.createFile(blob);
    return file.getUrl();
  } catch (err) {
    Logger.log('saveScreenshotToDrive_ error: ' + String(err && err.message ? err.message : err));
    return 'Gagal simpan gambar';
  }
}

function mapCategoryLabel_(code) {
  switch (String(code || '').toLowerCase()) {
    case 'bug':
    case 'bugreport':
      return 'Laporan Bug';
    case 'feature':
    case 'featurerequest':
      return 'Permintaan Fitur';
    case 'extension':
      return 'Kendala Ekstensi & Sumber';
    case 'performance':
      return 'Performa & Jaringan';
    case 'general':
      return 'Saran Umum';
    default:
      return code || 'Saran Umum';
  }
}

function createJsonResponse_(payload) {
  return ContentService
    .createTextOutput(JSON.stringify(payload))
    .setMimeType(ContentService.MimeType.JSON);
}
