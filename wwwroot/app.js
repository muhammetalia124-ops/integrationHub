'use strict';

const API_BASE = '/api';
const TOKEN_KEY = 'ih_token';
const USER_KEY = 'ih_user';
const REFRESH_TOKEN_KEY = 'ih_refresh_token';
const TOKEN_EXPIRES_KEY = 'ih_token_expires_at';
const MIN_CALCULATING_DISPLAY_MS = 400;
const LOCATION_TIMEOUT_MS = 45000;
const LOCATION_MAX_AGE_MS = 15 * 60 * 1000;
const LOCATION_AUTO_RETRY_DELAY_MS = 1200;
const TOKEN_REFRESH_LEAD_MS = 60 * 1000;
const TOKEN_REFRESH_MIN_DELAY_MS = 5000;

const el = (id) => document.getElementById(id);
const token = () => localStorage.getItem(TOKEN_KEY);
const isLoggedIn = () => !!token();
const refreshToken = () => localStorage.getItem(REFRESH_TOKEN_KEY);
const isGuest = () => !!readStoredUser().isGuest;

let categories = [];
let activeCategory = null;
let activeSub = null;
let authSessionVersion = 0;
let historyRequestVersion = 0;
let passwordUserId = null;
let sharedDataEditId = null;
let detectedLocation = null;
let locationRequestInProgress = false;
let locationRequestId = 0;
let locationRetryTimer = null;
let pendingDynamicValue = null;
let selectedSourceCurrencyKey = 'TRY';
let selectedGoldDirection = null;

async function api(path, { method = 'GET', body, retry = true, headers: extraHeaders } = {}) {
  const headers = { 'Content-Type': 'application/json', ...(extraHeaders || {}) };
  const t = token();
  if (t) headers['Authorization'] = `Bearer ${t}`;
  const res = await fetch(`${API_BASE}${path}`, {
    method,
    headers,
    cache: 'no-store',
    body: body ? JSON.stringify(body) : undefined
  });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    const msg = (data && data.error) ? data.error : `İstek başarısız (${res.status})`;
    if (res.status === 401 && retry && t && canRefreshSession(path) && await refreshSession()) {
      return api(path, { method, body, retry: false, headers: extraHeaders });
    }
    const error = new Error(msg);
    error.status = res.status;
    error.code = data && data.detail;
    throw error;
  }
  return data;
}

function canRefreshSession(path) {
  if (!refreshToken()) return false;
  return !path.startsWith('/auth/refresh') && !path.startsWith('/auth/login');
}

let refreshPromise = null;

async function refreshSession() {
  const stored = refreshToken();
  if (!stored) return false;
  if (refreshPromise) return refreshPromise;
  const sessionVersion = authSessionVersion;
  refreshPromise = (async () => {
    try {
      const data = await api('/auth/refresh', {
        method: 'POST',
        body: { refreshToken: stored },
        retry: false
      });
      if (sessionVersion !== authSessionVersion) return false;
      applyAuthData(data);
      scheduleTokenRefresh();
      return true;
    } catch (error) {
      if (sessionVersion === authSessionVersion) {
        toast('Oturumunuz sona erdi. Lütfen tekrar giriş yapın.', 'error');
        endSession();
      }
      return false;
    } finally {
      refreshPromise = null;
    }
  })();
  return refreshPromise;
}

let tokenRefreshTimer = null;

function clearTokenRefreshTimer() {
  if (tokenRefreshTimer) {
    clearTimeout(tokenRefreshTimer);
    tokenRefreshTimer = null;
  }
}

function scheduleTokenRefresh() {
  clearTokenRefreshTimer();
  if (!refreshToken()) return;
  const expiresAtRaw = Number(localStorage.getItem(TOKEN_EXPIRES_KEY));
  if (!expiresAtRaw) return;
  const delay = Math.max(TOKEN_REFRESH_MIN_DELAY_MS, expiresAtRaw - Date.now() - TOKEN_REFRESH_LEAD_MS);
  tokenRefreshTimer = setTimeout(() => { void refreshSession(); }, delay);
}

function storeSession(data) {
  localStorage.setItem(TOKEN_KEY, data.token);
  const expiresAt = new Date(data.expiresAt).getTime();
  if (expiresAt) localStorage.setItem(TOKEN_EXPIRES_KEY, String(expiresAt));
  if (data.refreshToken) {
    localStorage.setItem(REFRESH_TOKEN_KEY, data.refreshToken);
  }
  storeUser(data);
}

function applyAuthData(data) {
  localStorage.setItem(TOKEN_KEY, data.token);
  const expiresAt = new Date(data.expiresAt).getTime();
  if (expiresAt) localStorage.setItem(TOKEN_EXPIRES_KEY, String(expiresAt));
  if (data.refreshToken) localStorage.setItem(REFRESH_TOKEN_KEY, data.refreshToken);
  storeUser({ ...readStoredUser(), ...data });
}

function storeUser(data) {
  localStorage.setItem(USER_KEY, JSON.stringify({
    id: data.userId,
    username: data.username,
    email: data.email,
    isAdmin: data.isAdmin,
    isGuest: !!data.isGuest,
    isEmailVerified: data.isEmailVerified !== false,
    usernameChangedAt: data.usernameChangedAt || null,
    usernameChangeAvailableAt: data.usernameChangeAvailableAt || null,
    canChangeUsername: data.canChangeUsername !== false,
    usernameChangeIntervalDays: data.usernameChangeIntervalDays || 30
  }));
}

function endSession() {
  authSessionVersion++;
  clearTokenRefreshTimer();
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(REFRESH_TOKEN_KEY);
  localStorage.removeItem(TOKEN_EXPIRES_KEY);
  localStorage.removeItem(USER_KEY);
  clearSessionUi();
  el('authScreen').classList.remove('hidden');
  playEntrance(el('authScreen').firstElementChild);
  setMode('login');
  el('authForm').reset();
  hideVerificationPanel();
}

function toast(message, kind = 'info') {
  const t = el('toast');
  const colors = {
    info: 'bg-slate-800 text-white',
    success: 'bg-emerald-600 text-white',
    error: 'bg-red-600 text-white'
  };
  t.className = `fixed bottom-6 left-1/2 -translate-x-1/2 z-50 px-4 py-2.5 rounded-xl text-sm shadow-lg ${colors[kind] || colors.info}`;
  t.textContent = message;
  t.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), 2800);
}

let toastTimer = null;

const SPINNER_SVG = '<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" class="mr-2 h-4 w-4 shrink-0 animate-spin" stroke="currentColor" stroke-width="2.5"><path stroke-linecap="round" d="M12 3a9 9 0 1 0 9 9"/></svg>';

function setButtonBusy(button, label) {
  if (!button) return;
  if (!button.dataset.originalHtml) {
    button.dataset.ownDisabled = String(button.disabled);
    button.dataset.originalHtml = button.innerHTML;
    button.dataset.originalDisabled = String(button.disabled);
  }
  button.disabled = true;
  button.classList.add('opacity-70', 'cursor-wait', 'inline-flex', 'items-center', 'justify-center');
  button.innerHTML = `${SPINNER_SVG}<span>${escapeHtml(label)}</span>`;
}

function clearButtonBusy(button) {
  if (!button || !button.dataset.originalHtml) return;
  button.classList.remove('opacity-70', 'cursor-wait', 'inline-flex', 'items-center', 'justify-center');
  button.innerHTML = button.dataset.originalHtml;
  const ownDisabled = button.dataset.ownDisabled;
  button.disabled = ownDisabled === 'true';
  delete button.dataset.ownDisabled;
  delete button.dataset.originalHtml;
  delete button.dataset.originalDisabled;
}

function hideToast() {
  clearTimeout(toastTimer);
  el('toast').classList.add('hidden');
}

function playEntrance(element) {
  element.classList.remove('screen-enter');
  void element.offsetWidth;
  element.classList.add('screen-enter');
}

let mode = 'login';
function setMode(m) {
  mode = m;
  const isLogin = m === 'login';
  el('tabLogin').classList.toggle('bg-white', isLogin);
  el('tabLogin').classList.toggle('shadow-sm', isLogin);
  el('tabLogin').classList.toggle('text-brand-700', isLogin);
  el('tabLogin').classList.toggle('text-slate-500', !isLogin);
  el('tabRegister').classList.toggle('bg-white', !isLogin);
  el('tabRegister').classList.toggle('shadow-sm', !isLogin);
  el('tabRegister').classList.toggle('text-brand-700', !isLogin);
  el('tabRegister').classList.toggle('text-slate-500', isLogin);
  el('usernameField').classList.toggle('hidden', isLogin);
  el('username').required = !isLogin;
  el('confirmPasswordField').classList.toggle('hidden', isLogin);
  el('confirmPassword').required = !isLogin;
  el('forgotPasswordRow').classList.toggle('hidden', !isLogin);
  el('authSubmit').textContent = isLogin ? 'Giriş Yap' : 'Kayıt Ol';
  el('password').autocomplete = isLogin ? 'current-password' : 'new-password';
  el('authError').classList.add('hidden');
  hideVerificationPanel();
}

function showVerificationPanel(email, message, devCode) {
  el('verificationIntro').textContent = message ||
    'E-posta adresinize 6 haneli bir doğrulama kodu ve bağlantı gönderdik. Kodu aşağıya girin veya e-postadaki bağlantıya tıklayın.';
  el('verificationIntro').textContent += ` (${email})`;
  el('verificationCode').value = '';
  el('verificationError').classList.add('hidden');
  if (devCode) {
    el('verificationCode').value = devCode;
    el('verificationError').textContent = `Geliştirme ortamı: e-posta gönderimi yapılandırılmadı, doğrulama kodu otomatik dolduruldu (${devCode}).`;
    el('verificationError').className = 'text-sm text-amber-700 bg-amber-50 rounded-lg px-3 py-2';
  }
  el('emailVerificationPanel').classList.remove('hidden');
  el('authForm').classList.add('hidden');
  el('emailVerificationPanel').dataset.email = email;
  el('verificationCode').focus();
}

function hideVerificationPanel() {
  el('emailVerificationPanel').classList.add('hidden');
  el('authForm').classList.remove('hidden');
  delete el('emailVerificationPanel').dataset.email;
}

async function submitVerificationCode(code) {
  const email = el('emailVerificationPanel').dataset.email;
  if (!email) return;
  const error = el('verificationError');
  error.className = 'hidden text-sm text-red-600';
  error.textContent = '';
  if (!/^\d{6}$/.test(code || '')) {
    error.className = 'text-sm text-red-600 bg-red-50 rounded-lg px-3 py-2';
    error.textContent = 'Lütfen e-postanızdaki 6 haneli doğrulama kodunu girin.';
    return;
  }
  setButtonBusy(el('verificationSubmit'), 'Doğrulanıyor...');
  try {
    await api('/auth/verify-email', { method: 'POST', body: { email, code }, retry: false });
    hideVerificationPanel();
    toast('E-posta adresiniz doğrulandı. Şimdi şifrenizle giriş yapın.', 'success');
    setMode('login');
    el('email').value = email;
    el('password').value = '';
    el('authError').className = 'text-sm text-emerald-700 bg-emerald-50 rounded-lg px-3 py-2';
    el('authError').textContent = 'E-posta adresiniz doğrulandı. Şimdi şifrenizle giriş yapabilirsiniz.';
    el('authError').classList.remove('hidden');
    el('password').focus();
  } catch (err) {
    error.className = 'text-sm text-red-600 bg-red-50 rounded-lg px-3 py-2';
    error.textContent = err.message;
  } finally {
    clearButtonBusy(el('verificationSubmit'));
  }
}

el('verificationForm').addEventListener('submit', (event) => {
  event.preventDefault();
  void submitVerificationCode(el('verificationCode').value.trim());
});

el('resendVerificationBtn').onclick = async () => {
  const email = el('emailVerificationPanel').dataset.email;
  if (!email) return;
  const error = el('verificationError');
  setButtonBusy(el('resendVerificationBtn'), 'Gönderiliyor...');
  try {
    const result = await api('/auth/resend-verification', { method: 'POST', body: { email }, retry: false });
    error.className = 'text-sm text-emerald-700 bg-emerald-50 rounded-lg px-3 py-2';
    error.textContent = result.message || 'Doğrulama e-postası yeniden gönderildi.';
    if (result.devVerificationCode) {
      el('verificationCode').value = result.devVerificationCode;
    }
  } catch (err) {
    error.className = 'text-sm text-red-600 bg-red-50 rounded-lg px-3 py-2';
    error.textContent = err.message;
  } finally {
    clearButtonBusy(el('resendVerificationBtn'));
  }
};

el('forgotPasswordBtn').onclick = () => openRecovery(el('email').value.trim(), null);

el('guestLoginBtn').onclick = async () => {
  const button = el('guestLoginBtn');
  if (button.disabled) return;
  el('authError').classList.add('hidden');
  setButtonBusy(button, 'Misafir oturumu açılıyor...');
  try {
    const data = await api('/auth/guest', { method: 'POST', retry: false });
    authSessionVersion++;
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    storeSession(data);
    toast('Misafir olarak giriş yaptınız. İşlemler kaydedilmeyecek.', 'info');
    await enterApp(authSessionVersion);
  } catch (err) {
    el('authError').textContent = err.message;
    el('authError').className = 'text-sm text-red-600 bg-red-50 rounded-lg px-3 py-2';
    el('authError').classList.remove('hidden');
  } finally {
    clearButtonBusy(button);
  }
};

function validEmail(value) {
  return /^[^\s@]+@[^\s@]+\.[A-Za-z]{2,}$/.test(value);
}

el('tabLogin').onclick = () => { hideVerificationPanel(); setMode('login'); };
el('tabRegister').onclick = () => { hideVerificationPanel(); setMode('register'); };

el('authForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  el('authError').classList.add('hidden');
  const submitMode = mode;
  const email = el('email').value.trim();
  const password = el('password').value;
  const username = el('username').value.trim();
  if (!validEmail(email)) {
    el('authError').textContent = 'Lütfen geçerli bir e-posta adresi girin (ör. ad@ornek.com).';
    el('authError').classList.remove('hidden');
    return;
  }
  if (!password || (submitMode === 'register' && (password.length < 6 || username.length < 3))) {
    el('authError').textContent = submitMode === 'register'
      ? 'Kullanıcı adı en az 3, şifre en az 6 karakter olmalıdır.'
      : 'Lütfen e-posta ve şifre alanlarını doldurun.';
    el('authError').classList.remove('hidden');
    return;
  }
  if (submitMode === 'register' && password !== el('confirmPassword').value) {
    el('authError').textContent = 'Şifreler birbiriyle eşleşmiyor.';
    el('authError').classList.remove('hidden');
    return;
  }
  el('tabLogin').disabled = true;
  el('tabRegister').disabled = true;
  setButtonBusy(el('authSubmit'), submitMode === 'login' ? 'Giriş yapılıyor...' : 'Kayıt yapılıyor...');
  try {
    const payload = submitMode === 'register'
      ? { email, password, username }
      : { email, password };

    const data = await api(`/auth/${submitMode}`, { method: 'POST', body: payload, retry: false });

    if (submitMode === 'register') {
      setMode('login');
      el('email').value = email;
      showVerificationPanel(email, data.message, data.devVerificationCode);
      toast('Kayıt oluşturuldu. E-postanıza gelen kod ile doğrulayın.', 'success');
      return;
    }

    authSessionVersion++;
    storeSession(data);
    scheduleTokenRefresh();
    await enterApp(authSessionVersion);
  } catch (err) {
    el('authError').className = 'text-sm text-red-600 bg-red-50 rounded-lg px-3 py-2';
    el('authError').textContent = err.message;
    el('authError').classList.remove('hidden');
    if (err.code === 'email-not-verified') {
      showVerificationPanel(email, err.message);
    }
  } finally {
    el('tabLogin').disabled = false;
    el('tabRegister').disabled = false;
    clearButtonBusy(el('authSubmit'));
    el('authSubmit').textContent = mode === 'login' ? 'Giriş Yap' : 'Kayıt Ol';
  }
});

function readStoredUser() {
  try {
    return JSON.parse(localStorage.getItem(USER_KEY) || '{}') || {};
  } catch {
    return {};
  }
}

function applyUsernameChangeState(stored) {
  const input = el('newUsernameInput');
  const submit = el('changeUsernameSubmit');
  const hint = el('usernameChangeHint');
  const canChange = stored.canChangeUsername !== false;
  const intervalDays = stored.usernameChangeIntervalDays || 30;

  input.disabled = !canChange;
  if (!canChange) {
    submit.disabled = true;
    submit.classList.add('opacity-60');
  } else {
    submit.disabled = false;
    submit.classList.remove('opacity-60');
  }

  if (canChange) {
    hint.textContent = stored.usernameChangedAt
      ? `Kullanıcı adınızı ${intervalDays} günde bir değiştirebilirsiniz. Şu an değişiklik yapabilirsiniz.`
      : `Kullanıcı adınızı ${intervalDays} günde yalnızca bir kez değiştirebilirsiniz.`;
    hint.className = 'text-xs text-slate-500 leading-5';
    input.placeholder = stored.username || '';
    return;
  }

  const availableAt = stored.usernameChangeAvailableAt ? new Date(stored.usernameChangeAvailableAt) : null;
  const daysLeft = availableAt ? Math.max(1, Math.ceil((availableAt.getTime() - Date.now()) / 86400000)) : intervalDays;
  hint.textContent = availableAt
    ? `Kullanıcı adınızı son 30 gün içinde değiştirdiniz. Yeni değişiklik tarihi: ${availableAt.toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' })} (~${daysLeft} gün kaldı).`
    : `Kullanıcı adınızı ${intervalDays} günde yalnızca bir kez değiştirebilirsiniz.`;
  hint.className = 'text-xs text-amber-700 leading-5';
  input.placeholder = stored.username || '';
}

let recoveryState = { step: 'request', email: '', token: null };

function setRecoveryStep(step) {
  recoveryState.step = step;
  const isRequest = step === 'request';
  const isReset = step === 'reset';
  el('recoveryCodeField').classList.toggle('hidden', !isReset);
  el('recoveryPasswordField').classList.toggle('hidden', !isReset);
  el('recoveryEmail').readOnly = !isRequest;
  el('recoveryTitle').textContent = isRequest ? 'Şifremi Unuttum' : 'Yeni Şifre Belirle';
  el('recoverySubtitle').textContent = isRequest
    ? 'E-posta adresinizi girin, gerçek doğrulama bağlantısı gönderelim.'
    : 'E-postanıza gönderilen kodu girin veya bağlantıdaki anahtarı kullanarak yeni şifrenizi belirleyin.';
  el('recoverySubmit').textContent = isRequest ? 'Bağlantı gönder' : 'Şifreyi güncelle';
  el('recoveryError').classList.add('hidden');
  el('recoveryInfo').classList.add('hidden');
  if (!isReset) {
    el('recoveryPassword').value = '';
    el('recoveryPasswordConfirm').value = '';
    el('recoveryCode').value = '';
  }
}

function showRecoveryError(message) {
  el('recoveryError').textContent = message;
  el('recoveryError').classList.remove('hidden');
  el('recoveryInfo').classList.add('hidden');
}

function showRecoveryInfo(message) {
  el('recoveryInfo').textContent = message;
  el('recoveryInfo').classList.remove('hidden');
  el('recoveryError').classList.add('hidden');
}

function openRecovery(email, token) {
  el('authScreen').classList.add('hidden');
  el('recoveryScreen').classList.remove('hidden');
  playEntrance(el('recoveryScreen').firstElementChild);
  recoveryState = { step: token ? 'reset' : 'request', email: email || '', token: token || null };
  el('recoveryEmail').value = email || '';
  setRecoveryStep(recoveryState.step);
  if (recoveryState.step === 'reset') {
    showRecoveryInfo('Doğrulama bağlantınız algılandı. Yeni şifrenizi belirleyin veya kod alanına e-postanızdaki kodu yazın.');
    el('recoveryPassword').focus();
  } else {
    el('recoveryEmail').focus();
  }
}

function closeRecovery() {
  el('recoveryScreen').classList.add('hidden');
  el('authScreen').classList.remove('hidden');
  playEntrance(el('authScreen').firstElementChild);
  hideVerificationPanel();
  setMode('login');
}

el('recoveryBackBtn').onclick = closeRecovery;

el('recoveryForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const submit = el('recoverySubmit');
  if (submit.disabled) return;
  const email = el('recoveryEmail').value.trim();
  if (!validEmail(email)) {
    showRecoveryError('Lütfen geçerli bir e-posta adresi girin (ör. ad@ornek.com).');
    return;
  }

  if (recoveryState.step === 'request') {
    setButtonBusy(submit, 'Gönderiliyor...');
    try {
      const result = await api('/auth/forgot-password', { method: 'POST', body: { email }, retry: false });
      recoveryState.email = email;
      setRecoveryStep('reset');
      showRecoveryInfo(result.message || 'Şifre sıfırlama bağlantısı gönderildi. E-posta kutunuzu kontrol edin.');
      el('recoveryCode').focus();
    } catch (err) {
      showRecoveryError(err.message);
    } finally {
      clearButtonBusy(submit);
    }
    return;
  }

  const code = el('recoveryCode').value.trim();
  const newPassword = el('recoveryPassword').value;
  if (!recoveryState.token && !/^\d{6}$/.test(code)) {
    showRecoveryError('E-postanızdaki 6 haneli kodu girin veya e-postadaki bağlantıyı kullanın.');
    return;
  }
  if (newPassword.length < 6) {
    showRecoveryError('Yeni şifre en az 6 karakter olmalıdır.');
    return;
  }
  if (newPassword !== el('recoveryPasswordConfirm').value) {
    showRecoveryError('Şifreler birbiriyle eşleşmiyor.');
    return;
  }

  setButtonBusy(submit, 'Güncelleniyor...');
  try {
    const result = await api('/auth/reset-password', {
      method: 'POST',
      retry: false,
      body: {
        email: recoveryState.email || email,
        code: recoveryState.token ? null : code,
        token: recoveryState.token,
        newPassword
      }
    });
    recoveryState.token = null;
    closeRecovery();
    el('email').value = recoveryState.email || email;
    el('password').value = '';
    el('authError').className = 'text-sm text-emerald-700 bg-emerald-50 rounded-lg px-3 py-2';
    el('authError').textContent = result.message || 'Şifreniz güncellendi. Yeni şifrenizle giriş yapabilirsiniz.';
    el('authError').classList.remove('hidden');
    el('password').focus();
  } catch (err) {
    showRecoveryError(err.message);
  } finally {
    clearButtonBusy(submit);
  }
});

function closeDeleteConfirm() {
  const modal = el('confirmDeleteModal');
  if (modal) modal.classList.add('hidden');
  clearButtonBusy(el('confirmDeleteAccount'));
  const cancel = el('cancelDeleteAccount');
  if (cancel) cancel.disabled = false;
}

async function confirmDeleteAccount() {
  const button = el('confirmDeleteAccount');
  const error = el('deleteAccountError');
  if (button.disabled) return;

  const password = el('deleteAccountPassword').value;
  error.classList.add('hidden');
  if (!password) {
    error.textContent = 'Hesabınızı silmek için şifrenizi girin.';
    error.classList.remove('hidden');
    return;
  }

  setButtonBusy(button, 'Siliniyor...');
  el('cancelDeleteAccount').disabled = true;
  try {
    await api('/auth/delete-account', { method: 'POST', body: { password } });
    closeDeleteConfirm();
    el('accountSettingsModal').classList.add('hidden');
    endSession();
    toast('Hesabınız ve geçmiş işlemleriniz silindi.', 'success');
  } catch (err) {
    error.textContent = err.message;
    error.classList.remove('hidden');
    clearButtonBusy(button);
    el('cancelDeleteAccount').disabled = false;
  }
}

function clearSessionUi() {
  locationRequestId++;
  locationRequestInProgress = false;
  clearLocationRetryTimer();
  detectedLocation = null;
  activeCategory = null;
  activeSub = null;
  categories = [];
  passwordUserId = null;
  sharedDataEditId = null;
  el('categoryGrid').innerHTML = '';
  el('inputPanel').innerHTML = '';
  el('inputPanel').classList.add('hidden');
  el('sharedDataPanel').classList.add('hidden');
  el('appScreen').classList.add('hidden');
  el('topBar').classList.add('hidden');
  el('adminBtn').classList.add('hidden');
  el('adminModal').classList.add('hidden');
  el('adminHistoryModal').classList.add('hidden');
  el('passwordModal').classList.add('hidden');
  el('accountSettingsModal').classList.add('hidden');
  el('confirmDeleteModal').classList.add('hidden');
  el('deleteAccountPassword').value = '';
  el('deleteAccountError').classList.add('hidden');
  el('historyModal').classList.add('hidden');
  el('guestHistoryRequiredModal').classList.add('hidden');
  el('recoveryScreen').classList.add('hidden');
  el('emailVerificationPanel').classList.add('hidden');
  el('usernameChangeHint').textContent = '';
  el('changeUsernameError').classList.add('hidden');
  el('accountUsernameForm').reset();
  el('historyList').innerHTML = '';
  el('clearHistoryBtn').disabled = false;
  el('clearHistoryBtn').textContent = 'Geçmişi temizle';
  historyRequestVersion++;
  el('sharedDataList').innerHTML = '';
  el('sharedDataCount').classList.add('hidden');
  el('sharedDataType').value = '';
  el('sharedDataFrom').value = '';
  el('sharedDataTo').value = '';
  el('adminUsers').innerHTML = '';
  el('adminSharedList').innerHTML = '';
  el('adminHistoryList').innerHTML = '';
  el('adminHistoryTitle').textContent = 'Kullanıcı geçmişi';
  el('adminError').textContent = '';
  el('adminError').classList.add('hidden');
  el('createUserForm').reset();
  el('passwordForm').reset();
  el('adminSharedForm').reset();
  el('adminSharedPayload').value = '';
  el('adminSharedSave').textContent = 'Kaydı ekle';
  el('adminSharedCancel').classList.add('hidden');
  el('userBadge').querySelector('.user-badge-text').textContent = '';
  el('toast').classList.add('hidden');
  el('toast').textContent = '';
}

el('logoutBtn').onclick = () => {
  const storedRefresh = refreshToken();
  if (storedRefresh) {
    void api('/auth/logout', { method: 'POST', body: { refreshToken: storedRefresh }, retry: false })
      .catch(() => {});
  }
  endSession();
};

async function enterApp(sessionVersion = authSessionVersion) {
  try {
    const currentUser = await api('/auth/me');
    if (sessionVersion !== authSessionVersion) return;
    storeUser({ ...readStoredUser(), ...currentUser });
    const u = readStoredUser();
    if (currentUser.token) {
      localStorage.setItem(TOKEN_KEY, currentUser.token);
      const expiresAt = new Date(currentUser.expiresAt).getTime();
      if (expiresAt) localStorage.setItem(TOKEN_EXPIRES_KEY, String(expiresAt));
    }
    scheduleTokenRefresh();
    el('userBadge').querySelector('.user-badge-text').textContent = u.isGuest
      ? 'Merhaba, Misafir'
      : (u.username ? `Merhaba, ${u.username}` : '');
    el('adminBtn').classList.toggle('hidden', !u.isAdmin);
    el('authScreen').classList.add('hidden');
    el('appScreen').classList.remove('hidden');
    playEntrance(el('appScreen').firstElementChild);
    el('topBar').classList.remove('hidden');
    const meta = await api('/search/init');
    if (sessionVersion !== authSessionVersion) return;
    categories = meta.categories || [];
    renderCategories();
  } catch (err) {
    if (sessionVersion !== authSessionVersion) return;
    const message = err.message.toLowerCase();
    if (err.status === 401 || message.includes('token') || message.includes('kimlik') || message.includes('hesap')) {
      endSession();
    } else {
      toast('Kategoriler yüklenemedi: ' + err.message, 'error');
    }
  }
}

    el('adminBtn').onclick = async () => {
      el('adminModal').classList.remove('hidden');
      setAdminSection('users');
      await loadAdminUsers();
    };
    el('adminUsersTab').onclick = () => setAdminSection('users');
    el('adminDataTab').onclick = async () => {
      setAdminSection('shared');
      if (!el('adminSharedDate').value) {
        el('adminSharedDate').value = getSharedDataDefaultDate(el('adminSharedType').value);
      }
      await loadAdminSharedData();
    };
    el('closeAdmin').onclick = () => el('adminModal').classList.add('hidden');
    el('adminModal').addEventListener('click', (event) => {
      if (event.target === el('adminModal')) el('adminModal').classList.add('hidden');
    });
    el('closeAdminHistory').onclick = () => el('adminHistoryModal').classList.add('hidden');
    el('adminHistoryModal').addEventListener('click', (event) => {
      if (event.target === el('adminHistoryModal')) el('adminHistoryModal').classList.add('hidden');
    });
    el('cancelPassword').onclick = () => el('passwordModal').classList.add('hidden');
    el('accountSettingsBtn').onclick = () => {
      const stored = readStoredUser();
      el('accountUsername').textContent = stored.username || '-';
      el('accountEmail').textContent = stored.email || '- (misafir)';
      el('accountPasswordForm').reset();
      el('changePasswordError').classList.add('hidden');
      clearButtonBusy(el('changePasswordSubmit'));
      el('accountUsernameForm').reset();
      el('changeUsernameError').classList.add('hidden');
      clearButtonBusy(el('changeUsernameSubmit'));
      applyUsernameChangeState(stored);
      closeDeleteConfirm();
      el('accountSettingsModal').classList.remove('hidden');
    };
    el('closeAccountSettings').onclick = () => el('accountSettingsModal').classList.add('hidden');
    el('accountSettingsModal').addEventListener('click', (event) => {
      if (event.target === el('accountSettingsModal')) el('accountSettingsModal').classList.add('hidden');
    });
    el('deleteAccountBtn').addEventListener('click', () => {
      if (isGuest()) {
        toast('Bu işlem için kayıt olmanız gerekiyor.', 'error');
      }
    });
    el('confirmDeleteModal').addEventListener('click', (event) => {
      if (event.target === el('confirmDeleteModal')) closeDeleteConfirm();
    });
    document.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      if (!el('confirmDeleteModal').classList.contains('hidden')) {
        closeDeleteConfirm();
      } else if (!el('accountSettingsModal').classList.contains('hidden')) {
        el('accountSettingsModal').classList.add('hidden');
      }
    });
    el('accountUsernameForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const error = el('changeUsernameError');
      const submitButton = el('changeUsernameSubmit');
      if (submitButton.disabled) return;
      const newUsername = el('newUsernameInput').value.trim();
      error.classList.add('hidden');
      if (newUsername.length < 3) {
        error.textContent = 'Kullanıcı adı en az 3 karakter olmalıdır.';
        error.classList.remove('hidden');
        return;
      }
      if (newUsername === readStoredUser().username) {
        error.textContent = 'Yeni kullanıcı adınız mevcut kullanıcı adınızla aynı olamaz.';
        error.classList.remove('hidden');
        return;
      }

      setButtonBusy(submitButton, 'Güncelleniyor...');
      try {
        const data = await api('/auth/change-username', { method: 'POST', body: { newUsername } });
        applyAuthData(data);
        scheduleTokenRefresh();
        el('accountUsernameForm').reset();
        el('accountUsername').textContent = data.username || newUsername;
        el('userBadge').querySelector('.user-badge-text').textContent = `Merhaba, ${data.username}`;
        applyUsernameChangeState(readStoredUser());
        toast('Kullanıcı adınız güncellendi.', 'success');
      } catch (err) {
        error.textContent = err.message;
        error.classList.remove('hidden');
      } finally {
        clearButtonBusy(submitButton);
        applyUsernameChangeState(readStoredUser());
      }
    });

    el('accountPasswordForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const error = el('changePasswordError');
      const submitButton = el('changePasswordSubmit');
      if (submitButton.disabled) return;
      const currentPassword = el('currentPassword').value;
      const newPassword = el('newPassword').value;
      error.classList.add('hidden');
      if (!currentPassword) {
        error.textContent = 'Mevcut şifrenizi girin.';
        error.classList.remove('hidden');
        return;
      }
      if (newPassword.length < 6) {
        error.textContent = 'Yeni şifre en az 6 karakter olmalıdır.';
        error.classList.remove('hidden');
        return;
      }
      if (newPassword !== el('newPasswordConfirm').value) {
        error.textContent = 'Yeni şifreler birbiriyle eşleşmiyor.';
        error.classList.remove('hidden');
        return;
      }
      if (currentPassword === newPassword) {
        error.textContent = 'Yeni şifreniz mevcut şifrenizle aynı olamaz. Lütfen farklı bir şifre belirleyin.';
        error.classList.remove('hidden');
        return;
      }

      setButtonBusy(submitButton, 'Güncelleniyor...');
      try {
        const data = await api('/auth/change-password', {
          method: 'POST',
          body: { currentPassword, newPassword }
        });
        el('accountPasswordForm').reset();
        if (data.token) {
          applyAuthData(data);
          scheduleTokenRefresh();
        }
        toast('Şifreniz güncellendi.', 'success');
      } catch (err) {
        error.textContent = err.message;
        error.classList.remove('hidden');
      } finally {
        clearButtonBusy(submitButton);
      }
    });
    el('deleteAccountBtn').onclick = () => {
      el('deleteAccountPassword').value = '';
      el('deleteAccountError').classList.add('hidden');
      clearButtonBusy(el('confirmDeleteAccount'));
      el('cancelDeleteAccount').disabled = false;
      el('confirmDeleteModal').classList.remove('hidden');
      el('deleteAccountPassword').focus();
    };
    el('cancelDeleteAccount').onclick = closeDeleteConfirm;
    el('confirmDeleteAccount').onclick = confirmDeleteAccount;
    el('deleteAccountPassword').addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        confirmDeleteAccount();
      }
    });
    el('adminSharedFilter').onclick = loadAdminSharedData;
    el('adminSharedDeleteFiltered').onclick = deleteAdminSharedDataByFilter;
    el('adminSharedCancel').onclick = resetAdminSharedForm;
    el('adminSharedType').addEventListener('change', () => {
      if (!sharedDataEditId) {
        el('adminSharedDate').value = getSharedDataDefaultDate(el('adminSharedType').value);
      }
    });
    el('adminSharedForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      clearAdminError();
      let payload;
      try {
        payload = JSON.parse(el('adminSharedPayload').value);
      } catch {
        showAdminError('Veri içeriği geçerli JSON olmalıdır.');
        return;
      }

      const request = {
        dataType: el('adminSharedType').value,
        recordKey: el('adminSharedKey').value.trim(),
        displayName: el('adminSharedName').value.trim(),
        dataDate: el('adminSharedDate').value,
        payloadJson: JSON.stringify(payload)
      };
      try {
        setButtonBusy(el('adminSharedSave'), sharedDataEditId ? 'Güncelleniyor...' : 'Kaydediliyor...');
        await api(sharedDataEditId ? `/admin/shared-data/${sharedDataEditId}` : '/admin/shared-data', {
          method: sharedDataEditId ? 'PUT' : 'POST',
          body: request
        });
        toast(sharedDataEditId ? 'Ortak veri güncellendi.' : 'Ortak veri kaydedildi.', 'success');
        resetAdminSharedForm();
        await loadAdminSharedData();
      } catch (error) {
        showAdminError(error.message);
      } finally {
        clearButtonBusy(el('adminSharedSave'));
      }
    });
    el('adminSharedList').addEventListener('click', async (event) => {
      const button = event.target.closest('button[data-shared-action]');
      if (!button) return;
      const id = Number(button.closest('[data-shared-id]')?.dataset.sharedId);
      if (!id) return;

      clearAdminError();
      try {
        if (button.dataset.sharedAction === 'edit') {
          const record = await api(`/admin/shared-data/${id}`);
          sharedDataEditId = record.id;
          el('adminSharedType').value = record.dataType;
          el('adminSharedKey').value = record.recordKey;
          el('adminSharedName').value = record.displayName;
          el('adminSharedDate').value = record.dataDate;
          el('adminSharedPayload').value = JSON.stringify(JSON.parse(record.payloadJson), null, 2);
          el('adminSharedSave').textContent = 'Değişiklikleri kaydet';
          el('adminSharedCancel').classList.remove('hidden');
          el('adminSharedForm').scrollIntoView({ behavior: 'smooth', block: 'start' });
        } else if (button.dataset.sharedAction === 'delete') {
          if (!window.confirm('Bu ortak veri kaydı kalıcı olarak silinsin mi?')) return;
          await api(`/admin/shared-data/${id}`, { method: 'DELETE' });
          toast('Ortak veri silindi.', 'success');
          if (sharedDataEditId === id) resetAdminSharedForm();
          await loadAdminSharedData();
        }
      } catch (error) {
        showAdminError(error.message);
      }
    });
    el('createUserForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      clearAdminError();
      const email = el('newUserEmail').value.trim();
      if (!validEmail(email)) {
        showAdminError('Lütfen geçerli bir e-posta adresi girin (ör. ad@ornek.com).');
        return;
      }
      if (el('newUsername').value.trim().length < 3 || el('newUserPassword').value.length < 6) {
        showAdminError('Kullanıcı adı en az 3, şifre en az 6 karakter olmalıdır.');
        return;
      }
      try {
        setButtonBusy(el('createUserSubmit'), 'Oluşturuluyor...');
        await api('/admin/users', {
          method: 'POST',
          body: {
            email,
            username: el('newUsername').value.trim(),
            password: el('newUserPassword').value
          }
        });
        el('createUserForm').reset();
        toast('Kullanıcı oluşturuldu.', 'success');
        await loadAdminUsers();
      } catch (error) {
        showAdminError(error.message);
      } finally {
        clearButtonBusy(el('createUserSubmit'));
      }
    });

    el('adminUsers').addEventListener('click', async (event) => {
      const button = event.target.closest('button[data-action]');
      if (!button) return;
      const row = button.closest('[data-user-id]');
      if (!row) return;
      const userId = Number(row.dataset.userId);
      const action = button.dataset.action;
      clearAdminError();

      try {
        if (action === 'save') {
          const email = row.querySelector('[data-field="email"]').value.trim();
          if (!validEmail(email)) {
            showAdminError('Lütfen kullanıcı için geçerli bir e-posta adresi girin.');
            return;
          }
          setButtonBusy(button, 'Kaydediliyor...');
          await api(`/admin/users/${userId}`, {
            method: 'PUT',
            body: {
              email,
              username: row.querySelector('[data-field="username"]').value.trim(),
              isActive: row.querySelector('[data-field="active"]').checked,
              isAdmin: row.querySelector('[data-field="admin"]').checked
            }
          });
          toast('Kullanıcı bilgileri güncellendi.', 'success');
          await loadAdminUsers();
        } else if (action === 'password') {
          passwordUserId = userId;
          el('passwordModalTitle').textContent = `Şifre belirle: ${row.querySelector('[data-field="username"]').value}`;
          el('passwordError').classList.add('hidden');
          el('passwordForm').reset();
          el('passwordModal').classList.remove('hidden');
        } else if (action === 'history') {
          const items = await api(`/admin/users/${userId}/history`);
          el('adminHistoryTitle').textContent = `${row.querySelector('[data-field="username"]').value} - işlem geçmişi`;
          renderHistoryInto(el('adminHistoryList'), items, 'Bu kullanıcının işlem geçmişi yok.');
          el('adminHistoryModal').classList.remove('hidden');
        } else if (action === 'clear-history') {
          if (!window.confirm('Bu kullanıcının işlem geçmişi silinsin mi? Hesap silinmeyecek.')) return;
          await api(`/admin/users/${userId}/history`, { method: 'DELETE' });
          toast('İşlem geçmişi silindi.', 'success');
        } else if (action === 'delete') {
          if (!window.confirm('Kullanıcı ve işlem geçmişi kalıcı olarak silinsin mi?')) return;
          await api(`/admin/users/${userId}`, { method: 'DELETE' });
          toast('Kullanıcı silindi.', 'success');
          await loadAdminUsers();
        }
      } catch (error) {
        showAdminError(error.message);
      } finally {
        clearButtonBusy(button);
      }
    });

    el('passwordForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const password = el('adminPassword').value;
      const error = el('passwordError');
      error.classList.add('hidden');
      if (password.length < 6) {
        error.textContent = 'Şifre en az 6 karakter olmalıdır.';
        error.classList.remove('hidden');
        return;
      }
      if (password !== el('adminPasswordConfirm').value) {
        error.textContent = 'Şifreler birbiriyle eşleşmiyor.';
        error.classList.remove('hidden');
        return;
      }
      try {
        setButtonBusy(el('passwordSubmit'), 'Kaydediliyor...');
        await api(`/admin/users/${passwordUserId}/password`, {
          method: 'POST',
          body: { password }
        });
        el('passwordModal').classList.add('hidden');
        toast('Şifre güncellendi.', 'success');
      } catch (err) {
        error.textContent = err.message;
        error.classList.remove('hidden');
      } finally {
        clearButtonBusy(el('passwordSubmit'));
      }
    });

async function loadAdminUsers() {
      const list = el('adminUsers');
      list.innerHTML = '<div class="text-center text-slate-400 py-6">Yükleniyor...</div>';
      clearAdminError();
      try {
        const users = await api('/admin/users');
        if (!users.length) {
          list.innerHTML = '<div class="text-center text-slate-400 py-6">Kullanıcı bulunamadı.</div>';
          return;
        }
        list.innerHTML = '';
        users.forEach((user) => {
          const row = document.createElement('div');
          row.dataset.userId = user.id;
          row.className = 'border border-slate-200 rounded-xl p-3 space-y-3';
          row.innerHTML = `
            <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
              <label class="text-xs text-slate-500">Kullanıcı adı<input data-field="username" value="${escapeHtml(user.username)}" class="mt-1 w-full px-2.5 py-2 text-sm rounded-lg border border-slate-200"></label>
              <label class="text-xs text-slate-500">E-posta<input data-field="email" type="email" value="${escapeHtml(user.email)}" class="mt-1 w-full px-2.5 py-2 text-sm rounded-lg border border-slate-200"></label>
            </div>
            <div class="flex flex-wrap items-center gap-3 text-sm">
              <label class="flex items-center gap-1"><input data-field="active" type="checkbox" ${user.isActive ? 'checked' : ''}> Etkin</label>
              <label class="flex items-center gap-1"><input data-field="admin" type="checkbox" ${user.isAdmin ? 'checked' : ''}> Yönetici</label>
              <span class="text-xs text-slate-400">Kayıt: ${formatDate(user.createdAt)}</span>
            </div>
            <div class="flex flex-wrap gap-2">
              <button data-action="save" class="px-3 py-1.5 rounded-lg bg-brand-600 text-white text-xs">Kaydet</button>
              <button data-action="password" class="px-3 py-1.5 rounded-lg bg-slate-100 text-slate-700 text-xs">Şifre belirle</button>
              <button data-action="history" class="px-3 py-1.5 rounded-lg bg-slate-100 text-slate-700 text-xs">Geçmiş</button>
              <button data-action="clear-history" class="px-3 py-1.5 rounded-lg bg-amber-50 text-amber-700 text-xs">Geçmişi sil</button>
              <button data-action="delete" class="px-3 py-1.5 rounded-lg bg-red-50 text-red-600 text-xs">Kullanıcıyı sil</button>
            </div>`;
          list.appendChild(row);
        });
      } catch (error) {
        list.innerHTML = '';
        showAdminError(error.message);
      }
    }

function setAdminSection(section) {
  const isShared = section === 'shared';
  el('adminUsersSection').classList.toggle('hidden', isShared);
  el('adminSharedSection').classList.toggle('hidden', !isShared);
  el('adminUsersTab').className = `px-3 py-2 rounded-lg text-sm ${isShared ? 'bg-slate-100 text-slate-700' : 'bg-brand-600 text-white'}`;
  el('adminDataTab').className = `px-3 py-2 rounded-lg text-sm ${isShared ? 'bg-brand-600 text-white' : 'bg-slate-100 text-slate-700'}`;
  clearAdminError();
}

function resetAdminSharedForm() {
  sharedDataEditId = null;
  el('adminSharedForm').reset();
  el('adminSharedDate').value = getSharedDataDefaultDate(el('adminSharedType').value);
  el('adminSharedPayload').value = '';
  el('adminSharedSave').textContent = 'Kaydı ekle';
  el('adminSharedCancel').classList.add('hidden');
}

function getSharedDataDefaultDate(type) {
  const offsetMs = type === 'prayer' ? 3 * 60 * 60 * 1000 : 0;
  return new Date(Date.now() + offsetMs).toISOString().slice(0, 10);
}

async function loadAdminSharedData() {
  const list = el('adminSharedList');
  list.innerHTML = '<div class="text-center text-slate-400 py-6">Yükleniyor...</div>';
  clearAdminError();
  const query = buildSharedDataQuery('adminSharedFilterType', 'adminSharedFrom', 'adminSharedTo');
  try {
    const records = await api(`/admin/shared-data?${query}`);
    renderSharedDataInto(list, records, 'Bu filtrelerde ortak veri bulunamadı.', true);
  } catch (error) {
    list.innerHTML = '';
    showAdminError(error.message);
  }
}

function describeAdminSharedFilter() {
  const typeLabel = el('adminSharedFilterType').selectedOptions[0]?.textContent?.trim() || 'Tümü';
  const from = el('adminSharedFrom').value;
  const to = el('adminSharedTo').value;
  let dateLabel = 'tüm tarihler';
  if (from && to) dateLabel = `${from} - ${to}`;
  else if (from) dateLabel = `${from} ve sonrası`;
  else if (to) dateLabel = `${to} ve öncesi`;
  return { typeLabel, dateLabel };
}

async function deleteAdminSharedDataByFilter() {
  const button = el('adminSharedDeleteFiltered');
  if (button.disabled) return;

  if (!el('adminSharedFilterType').value && !el('adminSharedFrom').value && !el('adminSharedTo').value) {
    showAdminError('Toplu silme için önce bir veri türü veya tarih aralığı seçip filtreleyin.');
    return;
  }

  clearAdminError();
  const query = buildSharedDataQuery('adminSharedFilterType', 'adminSharedFrom', 'adminSharedTo');

  let matches;
  try {
    matches = await api(`/admin/shared-data?${query}`);
  } catch (error) {
    showAdminError(error.message);
    return;
  }

  if (!Array.isArray(matches) || matches.length === 0) {
    toast('Bu filtrelerle silinecek kayıt bulunamadı.', 'info');
    return;
  }

  const { typeLabel, dateLabel } = describeAdminSharedFilter();
  const confirmed = window.confirm(
    `${matches.length} ortak veri kaydı kalıcı olarak silinecek.\n\n` +
    `Veri türü: ${typeLabel}\nTarih: ${dateLabel}\n\nDevam etmek istiyor musunuz? Bu işlem geri alınamaz.`
  );
  if (!confirmed) return;

  setButtonBusy(button, 'Siliniyor...');
  try {
    const result = await api(`/admin/shared-data?${query}`, { method: 'DELETE' });
    const deleted = result && typeof result.deleted === 'number' ? result.deleted : matches.length;
    if (sharedDataEditId && !matches.some((record) => record.id === sharedDataEditId)) {
      resetAdminSharedForm();
    }
    toast(`${deleted} ortak veri kaydı silindi.`, 'success');
    await loadAdminSharedData();
  } catch (error) {
    showAdminError(error.message);
  } finally {
    clearButtonBusy(button);
  }
}

function showAdminError(message) {
  el('adminError').textContent = message;
  el('adminError').classList.remove('hidden');
}

function clearAdminError() {
  el('adminError').textContent = '';
  el('adminError').classList.add('hidden');
}

const ICONS = {
  currency: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="w-6 h-6"><circle cx="12" cy="12" r="9"/><path d="M14.5 9.5c-.5-.8-1.5-1.3-2.5-1.3-1.4 0-2.5.8-2.5 1.8s1 1.5 2.5 1.8 2.5.9 2.5 1.9-1.1 1.8-2.5 1.8c-1 0-2-.5-2.5-1.3M12 7v1.2M12 14.8V16"/></svg>',
  gold: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="w-6 h-6"><circle cx="12" cy="12" r="9"/><path d="M9 15V9h3a2 2 0 010 4H9m0 0h3"/></svg>',
  weather: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="w-6 h-6"><path d="M17 18a4 4 0 100-8 5 5 0 00-9.8 1.5A3.5 3.5 0 007 18h10z"/></svg>',
  prayer: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="w-6 h-6"><circle cx="12" cy="12" r="9"/><path stroke-linecap="round" stroke-linejoin="round" d="M12 7v5l3 2"/></svg>',
  calculator: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" class="w-6 h-6"><rect x="5" y="3" width="14" height="18" rx="2"/><path d="M9 7h6M9 11h2M13 11h2M9 15h2M13 15h2"/></svg>',
  shared: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="w-6 h-6"><path d="M5 7.5 12 4l7 3.5-7 3.5-7-3.5Z"/><path d="m5 12 7 3.5 7-3.5M5 16.5l7 3.5 7-3.5"/></svg>'
};

function renderCategories() {
  try {
    renderCategoryTiles();
  } catch (err) {
    toast('Kategoriler yüklenemedi: ' + (err && err.message ? err.message : err), 'error');
    throw err;
  }
}

function renderCategoryTiles() {
  const grid = el('categoryGrid');
  grid.innerHTML = '';
  addSharedDataTile(grid);
  categories.forEach((cat) => {
    const icon = ICONS[cat.icon] || ICONS.calculator;
    const card = document.createElement('button');
    card.className = 'category-card group bg-white rounded-2xl shadow-sm border border-slate-200/70 hover:border-brand-400 hover:shadow-md transition p-5 text-left flex flex-col gap-3';
    card.innerHTML = `
      <div class="category-card-icon w-11 h-11 rounded-xl bg-brand-50 text-brand-600 grid place-items-center group-hover:bg-brand-600 group-hover:text-white transition">${icon}</div>
      <div class="category-card-copy">
        <div class="font-semibold text-slate-800">${escapeHtml(cat.displayName)}</div>
        <div class="text-xs text-slate-400 mt-0.5">${cat.serviceType === 'currency'
          ? 'Kur hesaplama'
          : cat.serviceType === 'gold'
            ? 'Altın hesaplama'
            : cat.subCategories.length === 1
              ? escapeHtml(cat.subCategories[0].displayName)
              : `${cat.subCategories.length} alt kategori`}</div>
      </div>
      <span class="category-card-arrow" aria-hidden="true">›</span>`;
    card.onclick = () => selectCategory(cat, card);
    grid.appendChild(card);
  });
}

const SHARED_DATA_CATEGORY_KEY = 'shared-data';

function addSharedDataTile(grid) {
  const tile = document.createElement('button');
  tile.id = 'sharedDataTile';
  tile.type = 'button';
  tile.dataset.categoryKey = SHARED_DATA_CATEGORY_KEY;
  tile.className = 'category-card group bg-white rounded-2xl shadow-sm border border-slate-200/70 hover:border-brand-400 hover:shadow-md transition p-5 text-left flex flex-col gap-3';
  tile.innerHTML = `
    <div class="category-card-icon w-11 h-11 rounded-xl bg-brand-50 text-brand-600 grid place-items-center group-hover:bg-brand-600 group-hover:text-white transition">${ICONS.shared}</div>
    <div class="category-card-copy">
      <div class="font-semibold text-slate-800">Ortak Veriler</div>
      <div class="text-xs text-slate-400 mt-0.5">Tüm kayıtları görüntüle</div>
    </div>
    <span class="category-card-arrow" aria-hidden="true">›</span>`;
  tile.onclick = () => selectSharedDataTile(tile);
  grid.appendChild(tile);
}

function selectSharedDataTile(tile) {
  activeCategory = null;
  activeSub = null;
  el('inputPanel').classList.add('hidden');
  el('sharedDataPanel').classList.remove('hidden');
  highlightCategoryTile(tile);
  playEntrance(el('sharedDataPanel'));
  el('sharedDataPanel').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  loadUserSharedData();
}

function closeSharedDataPanel() {
  el('sharedDataPanel').classList.add('hidden');
  highlightCategoryTile(null);
}

function highlightCategoryTile(tile) {
  document.querySelectorAll('#categoryGrid > button').forEach((b) => {
    const selected = b === tile;
    b.classList.toggle('ring-2', selected);
    b.classList.toggle('ring-brand-400', selected);
  });
}

function selectCategory(cat, card) {
  activeCategory = cat;
  activeSub = cat.subCategories.length === 1 || cat.serviceType === 'currency' || cat.serviceType === 'gold'
    ? cat.subCategories[0]
    : null;
  el('sharedDataPanel').classList.add('hidden');
  highlightCategoryTile(card);
  renderSubCategories();
  playEntrance(el('inputPanel'));
  el('inputPanel').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

function createCurrencyPicker(labelText, allEntries, initialKey, onSelect, idPrefix) {
  if (!idPrefix) idPrefix = 'currency';
  const field = document.createElement('div');
  field.className = 'relative z-20';
  field.dataset.pickerId = idPrefix;
  const label = document.createElement('label');
  label.className = 'block text-sm font-medium text-slate-600 mb-1';
  label.textContent = labelText;
  field.appendChild(label);

  const trigger = document.createElement('button');
  trigger.type = 'button';
  trigger.className = 'flex w-full items-center justify-between rounded-xl border border-slate-200 bg-white px-3.5 py-2.5 text-left text-sm text-slate-700 shadow-sm transition hover:border-brand-300 focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-100';
  trigger.setAttribute('aria-haspopup', 'listbox');
  trigger.setAttribute('aria-expanded', 'false');

  const labelFor = (entry) => `${entry.displayName} (${entry.key})`;
  let selectedKey = initialKey;
  const selectedLabel = document.createElement('span');
  selectedLabel.className = 'font-medium';
  selectedLabel.textContent = labelFor(allEntries.find((entry) => entry.key === initialKey) || allEntries[0]);
  const chevron = document.createElement('span');
  chevron.className = 'ml-3 text-slate-400';
  chevron.setAttribute('aria-hidden', 'true');
  chevron.textContent = '▾';
  trigger.append(selectedLabel, chevron);
  field.appendChild(trigger);

  const options = document.createElement('div');
  options.id = `${idPrefix}Options`;
  options.className = 'absolute left-0 right-0 top-full z-50 mt-2 hidden max-h-60 overflow-y-auto rounded-xl border border-slate-200 bg-white p-1.5 shadow-xl';
  options.setAttribute('role', 'listbox');
  options.setAttribute('aria-label', labelText);

  const optionButtons = [];
  allEntries.forEach((entry) => {
    const option = document.createElement('button');
    option.type = 'button';
    option.className = 'flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm transition hover:bg-brand-50 hover:text-brand-700';
    option.setAttribute('role', 'option');
    option.setAttribute('aria-selected', String(entry.key === selectedKey));
    option.dataset.optionKey = entry.key;
    if (entry.key === selectedKey) {
      option.classList.add('bg-brand-50', 'text-brand-700');
    }
    option.textContent = entry.displayName;
    const code = document.createElement('span');
    code.className = 'ml-3 text-xs text-slate-400';
    code.textContent = entry.key;
    option.appendChild(code);
    option.addEventListener('click', () => {
      selectedKey = entry.key;
      selectedLabel.textContent = labelFor(entry);
      optionButtons.forEach((button) => {
        const selected = button.dataset.optionKey === entry.key;
        button.setAttribute('aria-selected', String(selected));
        button.classList.toggle('bg-brand-50', selected);
        button.classList.toggle('text-brand-700', selected);
      });
      closeOptions();
      onSelect(entry);
    });
    optionButtons.push(option);
    options.appendChild(option);
  });

  let outsidePointerHandler = null;
  const closeOptions = () => {
    options.classList.add('hidden');
    trigger.setAttribute('aria-expanded', 'false');
    if (outsidePointerHandler) {
      document.removeEventListener('pointerdown', outsidePointerHandler, true);
      outsidePointerHandler = null;
    }
  };
  const openOptions = () => {
    if (!options.classList.contains('hidden')) return;
    options.classList.remove('hidden');
    trigger.setAttribute('aria-expanded', 'true');
    outsidePointerHandler = (event) => {
      if (!field.contains(event.target)) closeOptions();
    };
    document.addEventListener('pointerdown', outsidePointerHandler, true);
  };

  trigger.setAttribute('aria-controls', options.id);
  trigger.addEventListener('click', () => {
    if (options.classList.contains('hidden')) openOptions();
    else closeOptions();
  });
  trigger.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    event.preventDefault();
    openOptions();
    const selectedIndex = allEntries.findIndex((entry) => entry.key === selectedKey);
    const nextIndex = event.key === 'ArrowDown'
      ? Math.min(selectedIndex + 1, optionButtons.length - 1)
      : Math.max(selectedIndex - 1, 0);
    optionButtons[nextIndex]?.focus();
  });
  options.addEventListener('keydown', (event) => {
    const currentIndex = optionButtons.indexOf(event.target);
    let nextIndex = currentIndex;
    if (event.key === 'ArrowDown') nextIndex = Math.min(currentIndex + 1, optionButtons.length - 1);
    else if (event.key === 'ArrowUp') nextIndex = Math.max(currentIndex - 1, 0);
    else if (event.key === 'Home') nextIndex = 0;
    else if (event.key === 'End') nextIndex = optionButtons.length - 1;
    else if (event.key === 'Escape') {
      event.preventDefault();
      closeOptions();
      trigger.focus();
      return;
    } else {
      return;
    }
    event.preventDefault();
    optionButtons[nextIndex]?.focus();
  });
  field.appendChild(options);

  return { field, trigger, options };
}

function setCurrencyPickerTriggerValue(idPrefix, entry) {
  const field = el('inputPanel')?.querySelector(`[data-picker-id="${idPrefix}"]`);
  const trigger = field?.querySelector('button');
  const label = trigger?.querySelector('span');
  if (label) label.textContent = `${entry.displayName} (${entry.key})`;
  field?.querySelectorAll('button[role="option"]').forEach((button) => {
    const selected = button.dataset.optionKey === entry.key;
    button.setAttribute('aria-selected', String(selected));
    button.classList.toggle('bg-brand-50', selected);
    button.classList.toggle('text-brand-700', selected);
  });
}

function getCurrencyPickerKey(idPrefix) {
  const field = el('inputPanel')?.querySelector(`[data-picker-id="${idPrefix}"]`);
  if (!field) return null;
  const selected = field.querySelector('button[role="option"][aria-selected="true"]');
  return selected?.dataset.optionKey || null;
}

function defaultTargetCurrencyKey(sourceKey) {
  if (!sourceKey || sourceKey === 'TRY') return 'USD';
  return 'TRY';
}

function renderSubCategories() {
  const panel = el('inputPanel');
  panel.classList.remove('hidden');
  panel.innerHTML = '';

  const title = document.createElement('div');
  title.className = 'flex items-center justify-between mb-1';
  title.innerHTML = `<h3 class="font-semibold text-slate-800">${escapeHtml(activeCategory.displayName)}</h3>
    <button id="closePanel" type="button" aria-label="Paneli kapat" title="Paneli kapat"
      class="grid h-9 w-9 place-items-center rounded-lg text-slate-400 transition hover:bg-slate-100 hover:text-slate-700 text-xl leading-none">&times;</button>`;
  panel.appendChild(title);

  if (activeCategory.serviceType === 'currency') {
    const currencyEntries = [...activeCategory.subCategories, { key: 'TRY', displayName: 'Türk Lirası' }];
    const resolveEntry = (key) => currencyEntries.find((entry) => entry.key === key) || currencyEntries[0];

    const sourcePicker = createCurrencyPicker(
      'Kendi para biriminiz',
      currencyEntries,
      resolveEntry(selectedSourceCurrencyKey).key,
      (entry) => {
        selectedSourceCurrencyKey = entry.key;
        const nextSub = activeCategory.subCategories.find((sub) => sub.key === entry.key);
        if (nextSub) activeSub = nextSub;
      },
      'currencySource'
    );

    const defaultTargetKey = selectedSourceCurrencyKey === 'TRY' ? 'USD' : 'TRY';
    const targetPicker = createCurrencyPicker(
      'Hedef para birimi',
      currencyEntries,
      resolveEntry(defaultTargetKey).key,
      () => {},
      'currencyTarget'
    );

    const currencyPair = document.createElement('div');
    currencyPair.className = 'currency-pair-grid mb-1';
    currencyPair.appendChild(sourcePicker.field);
    currencyPair.appendChild(targetPicker.field);
    panel.appendChild(currencyPair);
  }

  if (activeCategory.serviceType === 'gold') {
    const optionField = document.createElement('div');
    optionField.className = 'relative z-20';
    optionField.dataset.pickerId = 'currency';
    const label = document.createElement('label');
    label.className = 'block text-sm font-medium text-slate-600 mb-1';
    label.textContent = 'Altın türü';
    optionField.appendChild(label);

    const currencyEntries = activeCategory.subCategories;

    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'flex w-full items-center justify-between rounded-xl border border-slate-200 bg-white px-3.5 py-2.5 text-left text-sm text-slate-700 shadow-sm transition hover:border-brand-300 focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-100';
    trigger.setAttribute('aria-haspopup', 'listbox');
    trigger.setAttribute('aria-expanded', 'false');

    const selectedLabel = document.createElement('span');
    selectedLabel.className = 'font-medium';
    selectedLabel.textContent = activeSub.displayName;
    const chevron = document.createElement('span');
    chevron.className = 'ml-3 text-slate-400';
    chevron.setAttribute('aria-hidden', 'true');
    chevron.textContent = '▾';
    trigger.append(selectedLabel, chevron);
    optionField.appendChild(trigger);

    const options = document.createElement('div');
    options.id = 'goldTypeOptions';
    options.className = 'absolute left-0 right-0 top-full z-50 mt-2 hidden max-h-60 overflow-y-auto rounded-xl border border-slate-200 bg-white p-1.5 shadow-xl';
    options.setAttribute('role', 'listbox');
    options.setAttribute('aria-label', label.textContent);

    const optionButtons = [];
    currencyEntries.forEach((sub) => {
      const option = document.createElement('button');
      option.type = 'button';
      option.className = 'flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm transition hover:bg-brand-50 hover:text-brand-700';
      option.setAttribute('role', 'option');
      option.setAttribute('aria-selected', String(sub.key === activeSub.key));
      option.textContent = sub.displayName;
      const code = document.createElement('span');
      code.className = 'ml-3 text-xs text-slate-400';
      code.textContent = '';
      option.appendChild(code);
      option.addEventListener('click', () => {
        activeSub = sub;
        selectedLabel.textContent = sub.displayName;
        optionButtons.forEach((button) => {
          const selected = button.dataset.optionKey === sub.key;
          button.setAttribute('aria-selected', String(selected));
          button.classList.toggle('bg-brand-50', selected);
          button.classList.toggle('text-brand-700', selected);
        });
        closeOptions();
        el('inputPanel').querySelector('#dynamicForm')?.remove();
        buildDynamicForm(activeSub);
      });
      option.dataset.optionKey = sub.key;
      optionButtons.push(option);
      options.appendChild(option);
    });

    let outsidePointerHandler = null;
    const closeOptions = () => {
      options.classList.add('hidden');
      trigger.setAttribute('aria-expanded', 'false');
      if (outsidePointerHandler) {
        document.removeEventListener('pointerdown', outsidePointerHandler, true);
        outsidePointerHandler = null;
      }
    };
    const openOptions = () => {
      if (!options.classList.contains('hidden')) return;
      options.classList.remove('hidden');
      trigger.setAttribute('aria-expanded', 'true');
      outsidePointerHandler = (event) => {
        if (!optionField.contains(event.target)) closeOptions();
      };
      document.addEventListener('pointerdown', outsidePointerHandler, true);
    };

    trigger.setAttribute('aria-controls', options.id);
    trigger.addEventListener('click', () => {
      if (options.classList.contains('hidden')) openOptions();
      else closeOptions();
    });
    trigger.addEventListener('keydown', (event) => {
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
      event.preventDefault();
      openOptions();
      const selectedIndex = activeCategory.subCategories.findIndex((sub) => sub.key === activeSub.key);
      const nextIndex = event.key === 'ArrowDown'
        ? Math.min(selectedIndex + 1, optionButtons.length - 1)
        : Math.max(selectedIndex - 1, 0);
      optionButtons[nextIndex]?.focus();
    });
    options.addEventListener('keydown', (event) => {
      const currentIndex = optionButtons.indexOf(event.target);
      let nextIndex = currentIndex;
      if (event.key === 'ArrowDown') nextIndex = Math.min(currentIndex + 1, optionButtons.length - 1);
      else if (event.key === 'ArrowUp') nextIndex = Math.max(currentIndex - 1, 0);
      else if (event.key === 'Home') nextIndex = 0;
      else if (event.key === 'End') nextIndex = optionButtons.length - 1;
      else if (event.key === 'Escape') {
        event.preventDefault();
        closeOptions();
        trigger.focus();
        return;
      } else {
        return;
      }
      event.preventDefault();
      optionButtons[nextIndex]?.focus();
    });
    optionField.appendChild(options);
    panel.appendChild(optionField);
  }

  if (activeCategory.subCategories.length > 1 &&
      !['currency', 'gold'].includes(activeCategory.serviceType)) {
    const chips = document.createElement('div');
    chips.className = 'flex flex-wrap gap-2 mb-5';
    activeCategory.subCategories.forEach((sub) => {
      const c = document.createElement('button');
      c.type = 'button';
      c.className = 'px-3.5 py-1.5 rounded-full text-sm border transition ' +
        (activeSub === sub
          ? 'bg-brand-600 text-white border-brand-600'
          : 'bg-slate-50 text-slate-600 border-slate-200 hover:border-brand-300');
      c.textContent = sub.displayName;
      c.onclick = () => { activeSub = sub; renderSubCategories(); };
      chips.appendChild(c);
    });
    panel.appendChild(chips);
  }

  if (!activeSub) {
    if (activeCategory.subCategories.length > 0) {
      const hint = document.createElement('p');
      hint.className = 'text-sm text-slate-500';
      hint.textContent = 'Devam etmek için bir alt kategori seçin.';
      panel.appendChild(hint);
    }
    el('closePanel').onclick = () => { panel.classList.add('hidden'); activeCategory = null; activeSub = null; renderCategories(); };
    return;
  }

  buildDynamicForm(activeSub);
  el('closePanel').onclick = () => { panel.classList.add('hidden'); activeCategory = null; activeSub = null; renderCategories(); };
}

function buildDynamicForm(sub) {
  const panel = el('inputPanel');
  const existing = panel.querySelector('[data-dynamic-form]');
  if (existing) existing.remove();

  const form = document.createElement('form');
  form.id = `dynamicForm-${sub.key}`;
  form.dataset.dynamicForm = 'true';
  form.className = 'mt-2 space-y-4 animate-fade-in';
  form.noValidate = true;
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void handleCalculate();
  });

  let quantityField = null;
  let initialDirection = null;

  if (sub.inputKind === 'bidirectional' && sub.options.length) {
    const selectedClass = 'px-3 py-1.5 rounded-lg text-sm border transition bg-brand-50 border-brand-300 text-brand-700';
    const idleClass = 'px-3 py-1.5 rounded-lg text-sm border transition bg-slate-50 border-slate-200 text-slate-600 hover:border-brand-300';
    const dirWrap = document.createElement('div');
    dirWrap.className = 'flex flex-wrap gap-2';
    initialDirection = sub.options.some((opt) => opt.value === selectedGoldDirection)
      ? selectedGoldDirection
      : sub.options[0].value;
    const buttons = [];
    sub.options.forEach((opt) => {
      const b = document.createElement('button');
      b.type = 'button';
      b.dataset.direction = opt.value;
      b.className = opt.value === initialDirection ? selectedClass : idleClass;
      b.textContent = opt.label;
      b.onclick = () => {
        selectedGoldDirection = opt.value;
        buttons.forEach((x) => { x.className = x === b ? selectedClass : idleClass; });
        if (quantityField) {
          quantityField.classList.toggle('hidden', opt.value === 'to-count');
        }
      };
      buttons.push(b);
      dirWrap.appendChild(b);
    });
    form.appendChild(dirWrap);
  }

  const inputType = sub.inputKind === 'text' ? 'text' : 'number';
  const field = document.createElement('div');
  const supportsLocation = ['weather', 'prayer'].includes(activeCategory.serviceType);
  const inputLabel = activeCategory.serviceType === 'currency'
    ? 'Miktar'
    : (sub.inputLabel || 'Değer');
  field.innerHTML = `
    <div class="mb-1 flex items-center justify-between gap-2">
      <label for="dynInput" class="block text-sm font-medium text-slate-600">${escapeHtml(inputLabel)}</label>
      ${supportsLocation ? `
        <button id="locationPermissionBtn" type="button"
          class="inline-flex min-h-8 shrink-0 items-center gap-1.5 rounded-lg px-2.5 text-xs font-semibold text-brand-700 transition hover:bg-brand-50 disabled:cursor-wait disabled:opacity-60">
          <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" class="h-4 w-4">
            <circle cx="12" cy="12" r="7.5"/><circle cx="12" cy="12" r="2.5"/>
            <path stroke-linecap="round" d="M12 2v3M12 19v3M2 12h3M19 12h3"/>
          </svg>
          <span class="location-button-label">Konumumu al</span>
        </button>` : ''}
    </div>
    <input id="dynInput" type="${inputType}" placeholder="${escapeHtml(sub.inputPlaceholder || '')}"
      class="w-full px-3.5 py-2.5 rounded-xl border border-slate-200 focus:border-brand-500 focus:ring-2 focus:ring-brand-100 outline-none transition" />
    ${supportsLocation ? '<p id="locationStatus" class="mt-1 min-h-4 text-xs text-slate-500" aria-live="polite"></p>' : ''}`;
  const input = field.querySelector('#dynInput');
  if (pendingDynamicValue !== null) {
    input.value = pendingDynamicValue;
    pendingDynamicValue = null;
  } else {
    input.value = getSuggestedLocation(activeCategory.serviceType);
  }
  form.appendChild(field);

  if (sub.requiresQuantity) {
    const quantityFieldHtml = `
      <label for="dynQuantity" class="mb-1 block text-sm font-medium text-slate-600">${escapeHtml(sub.quantityLabel || 'Gram miktarı')}</label>
      <div class="relative">
        <input id="dynQuantity" type="number" min="0" step="0.01" inputmode="decimal" value="1"
          placeholder="${escapeHtml(sub.quantityPlaceholder || '')}"
          class="w-full rounded-xl border border-slate-200 px-3.5 py-2.5 pr-12 outline-none transition focus:border-brand-500 focus:ring-2 focus:ring-brand-100" />
        <span class="pointer-events-none absolute right-3.5 top-1/2 -translate-y-1/2 text-sm text-slate-400">gr</span>
      </div>`;
    quantityField = document.createElement('div');
    quantityField.innerHTML = quantityFieldHtml;
    if (initialDirection === 'to-count') {
      quantityField.classList.add('hidden');
    }
    form.appendChild(quantityField);
  }

  const btn = document.createElement('button');
  btn.id = 'calcBtn';
  btn.type = 'submit';
  btn.className = 'w-full py-2.5 rounded-xl bg-brand-600 hover:bg-brand-700 text-white font-medium transition shadow-sm disabled:opacity-60';
  btn.textContent = activeCategory.serviceType === 'weather'
    ? 'Hava Durumunu Göster'
    : activeCategory.serviceType === 'prayer'
      ? 'Namaz Vakitlerini Göster'
      : 'Hesapla';
  form.appendChild(btn);
  input.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' || event.isComposing) return;
    event.preventDefault();
    event.stopPropagation();
    void handleCalculate();
  });

  const resultSlot = document.createElement('div');
  resultSlot.style.minHeight = '68px';

  const result = document.createElement('div');
  result.id = 'dynResult';
  result.className = 'hidden rounded-xl bg-emerald-50 border border-emerald-200 px-4 py-3 text-emerald-800';
  resultSlot.appendChild(result);
  form.appendChild(resultSlot);

  panel.appendChild(form);
  input.focus({ preventScroll: true });
  const locationButton = form.querySelector('#locationPermissionBtn');
  if (locationButton) {
    locationButton.onclick = requestLocationAccess;
    if (detectedLocation) {
      form.querySelector('#locationStatus').textContent =
        `Bulunan konum: ${detectedLocation.city}, ${detectedLocation.district}. İsterseniz alanı değiştirebilirsiniz.`;
      form.querySelector('.location-button-label').textContent = 'Konumumu yenile';
    }
  }
}

async function handleCalculate() {
  if (!activeCategory || !activeSub) return;
  const btn = el('calcBtn');
  if (!btn || btn.disabled) return;
  const input = el('dynInput');
  const result = el('dynResult');
  const value = (input?.value || '').trim();
  if (!value) { toast('Lütfen bir değer girin.', 'error'); return; }
  hideToast();

  const selectedDirection = (group) => {
    if (group === '' && selectedGoldDirection) {
      const current = document.querySelector('#inputPanel button[data-direction].bg-brand-50');
      if (current) return current.dataset.direction;
      return selectedGoldDirection;
    }
    const buttons = [...document.querySelectorAll(`[data-dynamic-form] button[data-direction]${group}`)];
    const picked = buttons.find((b) => b.classList.contains('bg-brand-50')) || buttons[0];
    return picked ? picked.dataset.direction : null;
  };
  const direction = selectedDirection('') || 'to-tl';

  const payload = {
    serviceType: activeCategory.serviceType,
    subCategory: activeSub.key,
    inputValue: value,
    direction,
    city: activeSub.inputKind === 'text' ? value : null
  };

  if (activeCategory.serviceType === 'currency') {
    const sourceCode = getCurrencyPickerKey('currencySource') || selectedSourceCurrencyKey || 'TRY';
    const targetCode = getCurrencyPickerKey('currencyTarget')
      || (sourceCode === 'TRY' ? 'USD' : 'TRY');
    payload.sourceCurrency = sourceCode;
    payload.targetCurrency = targetCode;
    payload.subCategory = sourceCode === 'TRY' ? targetCode : sourceCode;
  }

  if (activeSub.requiresQuantity && direction !== 'to-count') {
    const quantity = (el('dynQuantity')?.value || '').trim();
    const parsed = Number(quantity.replace(',', '.'));
    if (!quantity || !Number.isFinite(parsed) || parsed <= 0) {
      toast('Lütfen geçerli bir gram miktarı girin.', 'error');
      return;
    }
    payload.quantity = quantity.replace(',', '.');
  }
  if (isUsingDetectedLocation(value)) {
    payload.latitude = detectedLocation.latitude;
    payload.longitude = detectedLocation.longitude;
  }

  const calculationStartedAt = performance.now();
  const requestSessionVersion = authSessionVersion;
  btn.disabled = true;
  setButtonBusy(btn, 'Hesaplanıyor...');
  result.classList.add('hidden');
  try {
    const wrapped = await api('/search/calculate', { method: 'POST', body: payload });
    const data = wrapped.data || wrapped;
    if (requestSessionVersion !== authSessionVersion) return;
    if (data.success) {
      result.classList.remove('hidden');
      result.className = 'rounded-xl bg-emerald-50 border border-emerald-200 px-4 py-3 text-emerald-800';
      result.innerHTML = `
        <div class="text-sm font-medium">${escapeHtml(data.resultLabel || 'Sonuç')}: <span class="font-semibold">${escapeHtml(data.resultValue || '')}</span></div>
        ${data.rawDetail ? `<div class="text-xs text-emerald-700/80 mt-1">${escapeHtml(data.rawDetail)}</div>` : ''}`;
    } else {
      result.classList.remove('hidden');
      result.className = 'rounded-xl bg-red-50 border border-red-200 px-4 py-3 text-red-700';
      result.textContent = data.message || 'Hesaplama başarısız.';
    }
  } catch (err) {
    if (requestSessionVersion !== authSessionVersion) return;
    result.classList.remove('hidden');
    result.className = 'rounded-xl bg-red-50 border border-red-200 px-4 py-3 text-red-700';
    result.textContent = err.message;
  } finally {
    const remainingDisplayTime = MIN_CALCULATING_DISPLAY_MS - (performance.now() - calculationStartedAt);
    if (remainingDisplayTime > 0) {
      await new Promise((resolve) => setTimeout(resolve, remainingDisplayTime));
    }
    if (requestSessionVersion === authSessionVersion) {
      clearButtonBusy(btn);
      btn.textContent = activeCategory?.serviceType === 'weather'
        ? 'Hava Durumunu Göster'
        : activeCategory?.serviceType === 'prayer'
          ? 'Namaz Vakitlerini Göster'
          : 'Hesapla';
      btn.disabled = false;
    }
  }
}

function getSuggestedLocation(serviceType) {
  if (!detectedLocation) return '';
  if (serviceType === 'weather') return detectedLocation.city;
  if (serviceType === 'prayer') return `${detectedLocation.city}, ${detectedLocation.district}`;
  return '';
}

function isUsingDetectedLocation(value) {
  const suggestedLocation = activeCategory ? getSuggestedLocation(activeCategory.serviceType) : '';
  return !!suggestedLocation && value.localeCompare(suggestedLocation, 'tr', { sensitivity: 'accent' }) === 0;
}

function clearLocationRetryTimer() {
  if (locationRetryTimer) {
    clearTimeout(locationRetryTimer);
    locationRetryTimer = null;
  }
}

function readDevicePosition(maximumAge) {
  return new Promise((resolve, reject) => {
    navigator.geolocation.getCurrentPosition(resolve, reject, {
      enableHighAccuracy: true,
      timeout: LOCATION_TIMEOUT_MS,
      maximumAge
    });
  });
}

async function readDevicePositionWithRetry() {
  try {
    return await readDevicePosition(0);
  } catch (error) {
    const retriable = error && (error.code === 2 || error.code === 3);
    if (!retriable) throw error;
    await new Promise((resolve) => {
      clearLocationRetryTimer();
      locationRetryTimer = setTimeout(() => {
        locationRetryTimer = null;
        resolve();
      }, LOCATION_AUTO_RETRY_DELAY_MS);
    });
    return await readDevicePosition(LOCATION_MAX_AGE_MS);
  }
}

async function requestLocationAccess() {
  if (locationRequestInProgress) {
    if (locationRetryTimer) {
      locationRequestId++;
      locationRequestInProgress = false;
      clearLocationRetryTimer();
    } else {
      return;
    }
  }

  const button = el('locationPermissionBtn');
  const status = el('locationStatus');
  if (!navigator.geolocation) {
    if (status) {
      status.textContent = 'Bu tarayıcı konum özelliğini desteklemiyor. Konumu elle girebilirsiniz.';
      status.className = 'mt-1 text-xs font-medium text-amber-700';
    }
    return;
  }

  locationRequestInProgress = true;
  const requestId = ++locationRequestId;
  const requestedServiceType = activeCategory?.serviceType;
  detectedLocation = null;
  if (el('dynInput')) el('dynInput').value = '';
  if (button) {
    button.disabled = true;
    button.querySelector('.location-button-label').textContent = 'Konum alınıyor...';
  }
  if (status) {
    status.textContent = 'Tarayıcının konum izni penceresini onaylayabilirsin.';
    status.className = 'mt-1 text-xs font-medium text-slate-500';
  }
  try {
    const position = await readDevicePositionWithRetry();
    if (requestId !== locationRequestId) return;

    const location = await api('/search/location', {
      method: 'POST',
      body: {
        latitude: position.coords.latitude,
        longitude: position.coords.longitude
      }
    });
    if (requestId !== locationRequestId) return;

    detectedLocation = {
      city: location.city,
      district: location.district,
      latitude: position.coords.latitude,
      longitude: position.coords.longitude
    };

    if (activeCategory?.serviceType === requestedServiceType) {
      const input = el('dynInput');
      const newValue = getSuggestedLocation(requestedServiceType);
      if (input && newValue) {
        input.value = newValue;
      }

      const currentStatus = el('locationStatus');
      if (currentStatus) {
        currentStatus.textContent = `Bulunan konum: ${location.city}, ${location.district}. İsterseniz alanı değiştirebilirsiniz.`;
        currentStatus.className = 'mt-1 text-xs font-medium text-emerald-700';
        el('locationPermissionBtn').querySelector('.location-button-label').textContent = 'Konumumu yenile';
      }
      input?.focus({ preventScroll: true });
    }
  } catch (error) {
    const currentStatus = el('locationStatus');
    if (requestId !== locationRequestId) return;
    if (currentStatus && activeCategory?.serviceType === requestedServiceType) {
      if (error && error.code === 1) {
        currentStatus.textContent = 'Konum izni verilmedi. Tarayıcı ayarlarından izin verebilir veya şehrinizi elle girebilirsiniz.';
      } else if (error && error.code === 3) {
        currentStatus.textContent = 'Konum belirlenemedi. Tekrar deneyebilir veya şehrinizi elle girebilirsiniz.';
      } else {
        currentStatus.textContent = error instanceof Error
          ? `Konum alınamadı: ${error.message}`
          : 'Konum alınamadı. Şehrinizi elle girebilirsiniz.';
      }
      currentStatus.className = 'mt-1 text-xs font-medium text-amber-700';
    }
  } finally {
    if (requestId === locationRequestId) {
      locationRequestInProgress = false;
      const currentButton = el('locationPermissionBtn');
      if (currentButton) {
        currentButton.disabled = false;
        if (!detectedLocation) {
          currentButton.querySelector('.location-button-label').textContent = 'Tekrar dene';
        }
      }
    }
  }
}

el('sharedDataFilter').onclick = loadUserSharedData;
el('sharedDataType').addEventListener('change', loadUserSharedData);
el('closeSharedPanel').onclick = closeSharedDataPanel;

async function loadUserSharedData() {
  const list = el('sharedDataList');
  const count = el('sharedDataCount');
  const requestSessionVersion = authSessionVersion;
  list.innerHTML = '<div class="text-center text-slate-400 py-6">Yükleniyor...</div>';
  count.classList.add('hidden');
  const query = buildSharedDataQuery('sharedDataType', 'sharedDataFrom', 'sharedDataTo');
  try {
    const records = await api(`/search/shared-data?${query}`);
    if (requestSessionVersion !== authSessionVersion) return;
    renderSharedDataInto(list, records, 'Bu filtrelerde ortak veri bulunamadı.');
    if (records && records.length > 0) {
      count.textContent = `${records.length} kayıt`;
      count.classList.remove('hidden');
    }
  } catch (error) {
    if (requestSessionVersion !== authSessionVersion) return;
    list.innerHTML = `<div class="text-center text-red-600 py-6">${escapeHtml(error.message)}</div>`;
  }
}

function applySharedDataDefaultRange() {
  if (el('sharedDataFrom').value || el('sharedDataTo').value) return;
  el('sharedDataFrom').value = '';
  el('sharedDataTo').value = '';
  syncDateHint(el('sharedDataFrom'));
  syncDateHint(el('sharedDataTo'));
}

function syncDateHint(input) {
  if (!input) return;
  input.classList.toggle('has-value', !!input.value);
}

document.querySelectorAll('input[type="date"][data-date-hint]').forEach((input) => {
  syncDateHint(input);
  input.addEventListener('input', () => syncDateHint(input));
  input.addEventListener('change', () => syncDateHint(input));
});

el('historyBtn').onclick = async () => {
  if (isGuest()) {
    el('guestHistoryRequiredModal').classList.remove('hidden');
    return;
  }
  el('historyModal').classList.remove('hidden');
  await loadPersonalHistory();
};

el('guestHistoryClose').onclick = () => el('guestHistoryRequiredModal').classList.add('hidden');
el('guestHistoryRequiredModal').addEventListener('click', (event) => {
  if (event.target === el('guestHistoryRequiredModal')) el('guestHistoryRequiredModal').classList.add('hidden');
});
el('guestHistoryRegister').onclick = () => {
  el('guestHistoryRequiredModal').classList.add('hidden');
  endSession();
  setMode('register');
  el('username').focus();
};

async function loadPersonalHistory() {
  const list = el('historyList');
  const requestVersion = ++historyRequestVersion;
  const sessionVersion = authSessionVersion;
  list.innerHTML = '<div class="text-center text-slate-400 py-8">Yükleniyor...</div>';
  try {
    const items = await api('/search/history');
    if (requestVersion !== historyRequestVersion || sessionVersion !== authSessionVersion) return;
    renderHistory(items);
  } catch (err) {
    if (requestVersion !== historyRequestVersion || sessionVersion !== authSessionVersion) return;
    list.innerHTML = `<div class="text-center text-red-600 py-6">${escapeHtml(err.message)}</div>`;
  }
}

el('clearHistoryBtn').onclick = async () => {
  if (!window.confirm('Kendi işlem geçmişiniz kalıcı olarak silinsin mi? Bu işlem ortak verileri veya diğer kullanıcıların geçmişini etkilemez.')) return;

  const button = el('clearHistoryBtn');
  const sessionVersion = authSessionVersion;
  historyRequestVersion++;
  setButtonBusy(button, 'Temizleniyor...');
  try {
    await api('/search/history', { method: 'DELETE' });
    if (sessionVersion !== authSessionVersion) return;
    el('historyList').innerHTML = '<div class="text-center text-slate-400 py-8">Henüz işlem geçmişiniz yok.</div>';
    toast('İşlem geçmişiniz temizlendi.', 'success');
  } catch (error) {
    if (sessionVersion !== authSessionVersion) return;
    el('historyList').innerHTML = `<div class="text-center text-red-600 py-6">${escapeHtml(error.message)}</div>`;
  } finally {
    if (sessionVersion === authSessionVersion) {
      clearButtonBusy(button);
    }
  }
};

el('closeHistory').onclick = () => el('historyModal').classList.add('hidden');
el('historyModal').addEventListener('click', (e) => {
  if (e.target === el('historyModal')) el('historyModal').classList.add('hidden');
});

function buildSharedDataQuery(typeId, fromId, toId) {
  const params = new URLSearchParams({ take: '500' });
  const type = el(typeId).value;
  const from = el(fromId).value;
  const to = el(toId).value;
  if (type) params.set('type', type);
  if (from) params.set('from', from);
  if (to) params.set('to', to);
  return params.toString();
}

function renderSharedDataInto(list, records, emptyMessage, isAdmin = false) {
  if (!records || records.length === 0) {
    list.innerHTML = `<div class="text-center text-slate-400 py-6">${escapeHtml(emptyMessage)}</div>`;
    return;
  }

  list.innerHTML = '';
  records.forEach((record) => {
    let payload;
    try {
      payload = JSON.parse(record.payloadJson);
    } catch (error) {
      console.error('Arşiv kaydının JSON içeriği okunamadı.', error);
      payload = null;
    }

    list.appendChild(createSharedDataCard(record, payload, isAdmin));
  });
}

function createSharedDataCard(record, payload, isAdmin) {
    const row = document.createElement('article');
    row.className = 'rounded-xl border border-slate-200 p-3 space-y-2';
    if (isAdmin) {
      row.dataset.sharedId = record.id;
    }
    const details = isAdmin
      ? payload
        ? `<details><summary class="cursor-pointer text-xs text-slate-500">Veri ayrıntısı</summary><pre class="mt-2 overflow-x-auto rounded-lg bg-slate-50 p-2 text-xs">${escapeHtml(JSON.stringify(payload, null, 2))}</pre></details>`
        : '<div class="text-xs text-red-600">Bu kaydın JSON verisi okunamadı.</div>'
      : '';
    const managedStatus = record.isAdminManaged
      ? record.dataDate === getSharedDataDefaultDate(record.dataType)
        ? 'Yönetici düzenlemesi · Bugünkü hesaplamalarda kullanılır'
        : 'Yönetici düzenlemesi'
      : '';
    row.innerHTML = `
      <div class="flex flex-wrap items-start justify-between gap-2">
        <div class="min-w-0">
          <div class="font-medium text-sm text-slate-800">${escapeHtml(record.displayName)}</div>
          <div class="text-xs text-slate-500">${escapeHtml(sharedDataTypeLabel(record.dataType))} · ${escapeHtml(record.recordKey)} · ${escapeHtml(formatDataDate(record.dataDate))}</div>
          ${managedStatus ? `<div class="mt-1 text-xs text-amber-700">${escapeHtml(managedStatus)}</div>` : ''}
        </div>
      </div>
      <div class="text-sm text-slate-700">${escapeHtml(sharedDataSummary(record, payload))}</div>
      ${details}
      ${isAdmin ? `<div class="flex gap-2 pt-1"><button data-shared-action="edit" type="button" class="px-3 py-1.5 rounded-lg bg-slate-100 text-slate-700 text-xs">Düzenle</button><button data-shared-action="delete" type="button" class="px-3 py-1.5 rounded-lg bg-red-50 text-red-600 text-xs">Sil</button></div>` : ''}`;
    return row;
}

function sharedDataTypeLabel(type) {
  return ({
    currency: 'Döviz',
    gold: 'Altın',
    weather: 'Hava durumu',
    prayer: 'Namaz vakitleri'
  })[type] || type;
}

function sharedDataSummary(record, payload) {
  if (!payload) return 'Veri içeriği okunamadı.';
  if (record.dataType === 'currency') {
    return `${sharedDataField(payload, 'code') || record.recordKey} · Alış ${sharedDataField(payload, 'forexBuying')} · Satış ${sharedDataField(payload, 'forexSelling')} TL`;
  }
  if (record.dataType === 'gold') {
    const buyingPrice = sharedDataField(payload, 'buyingPrice');
    const sellingPrice = sharedDataField(payload, 'sellingPrice');
    const currency = sharedDataField(payload, 'currency') || 'TRY';
    return buyingPrice !== undefined && sellingPrice !== undefined
      ? `Alış ${buyingPrice} ${currency} · Satış ${sellingPrice} ${currency}`
      : `Birim fiyat: ${sharedDataField(payload, 'unitPrice')} TL`;
  }
  if (record.dataType === 'weather') {
    const weatherData = sharedDataField(payload, 'weatherData');
    const current = weatherData && sharedDataField(weatherData, 'current');
    const resultValue = sharedDataField(payload, 'resultValue');
    if (!current) return resultValue || 'Hava durumu bilgisi';
    return `${resultValue} · Sıcaklık ${sharedDataField(current, 'temperature_2m')}°C · Hissedilen ${sharedDataField(current, 'apparent_temperature')}°C · Nem %${sharedDataField(current, 'relative_humidity_2m')} · Rüzgar ${sharedDataField(current, 'wind_speed_10m')} km/sa · Kod ${sharedDataField(current, 'weather_code')}`;
  }
  if (record.dataType === 'prayer') {
    return `İmsak ${sharedDataField(payload, 'imsak')} · Güneş ${sharedDataField(payload, 'gunes')} · Öğle ${sharedDataField(payload, 'ogle')} · İkindi ${sharedDataField(payload, 'ikindi')} · Akşam ${sharedDataField(payload, 'aksam')} · Yatsı ${sharedDataField(payload, 'yatsi')}`;
  }
  return 'Ortak veri kaydı';
}

function sharedDataField(value, name) {
  const entry = Object.entries(value || {}).find(([key]) => key.toLowerCase() === name.toLowerCase());
  return entry ? entry[1] : undefined;
}

function formatDataDate(value) {
  const date = new Date(`${value}T00:00:00`);
  return isNaN(date.getTime()) ? value : date.toLocaleDateString('tr-TR');
}

function renderHistory(items) {
  renderHistoryInto(el('historyList'), items, 'Henüz işlem geçmişiniz yok.');
}

function renderHistoryInto(list, items, emptyMessage) {
  if (!items || items.length === 0) {
    list.innerHTML = `<div class="text-center text-slate-400 py-8">${escapeHtml(emptyMessage)}</div>`;
    return;
  }
  list.innerHTML = '';
  items.forEach((it) => {
    const row = document.createElement('div');
    row.className = 'flex items-start justify-between gap-3 p-3 rounded-xl border border-slate-200 hover:bg-slate-50 transition';
    const svcLabel = serviceLabel(it.serviceType, it.subCategory);
    row.innerHTML = `
      <div class="min-w-0">
        <div class="font-medium text-slate-800 text-sm">${escapeHtml(svcLabel)}</div>
        ${it.inputLabel ? `<div class="text-xs text-slate-500 mt-0.5">${escapeHtml(it.inputLabel)}: ${escapeHtml(it.inputValue || '')}</div>` : ''}
        ${it.resultLabel ? `<div class="text-xs text-emerald-700 mt-0.5">${escapeHtml(it.resultLabel)}: ${escapeHtml(it.resultValue || '')}</div>` : ''}
      </div>
      <div class="text-xs text-slate-400 whitespace-nowrap">${formatDate(it.timestamp)}</div>`;
    list.appendChild(row);
  });
}

function serviceLabel(svc, sub) {
  const cat = categories.find((c) => c.serviceType === svc);
  if (!cat) return `${svc} / ${sub}`;
  const s = cat.subCategories.find((x) => x.key === sub);
  return `${cat.displayName} / ${s ? s.displayName : sub}`;
}

function formatDate(ts) {
  const d = new Date(ts);
  if (isNaN(d.getTime())) return ts;
  return d.toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' });
}

function escapeHtml(s) {
  if (s == null) return '';
  return String(s).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}

function handleEmailLink() {
  const params = new URLSearchParams(window.location.search);
  const action = params.get('action');
  const email = params.get('email') || '';
  const token = params.get('token') || '';
  if (!action || !token) return false;

  window.history.replaceState({}, '', window.location.pathname);
  openRecovery(email, token);
  if (action === 'verify-email') {
    void verifyEmailLink(email, token);
  }

  return true;
}

async function verifyEmailLink(email, token) {
  setRecoveryStep('reset');
  try {
    await api('/auth/verify-email', { method: 'POST', body: { email, token }, retry: false });
    closeRecovery();
    el('email').value = email;
    el('authError').className = 'text-sm text-emerald-700 bg-emerald-50 rounded-lg px-3 py-2';
    el('authError').textContent = 'E-posta adresiniz doğrulandı. Şimdi şifrenizle giriş yapabilirsiniz.';
    el('authError').classList.remove('hidden');
    el('password').focus();
  } catch (err) {
    showRecoveryError(err.message);
  }
}

if (!handleEmailLink()) {
  if (isLoggedIn()) {
    enterApp();
  } else {
    setMode('login');
  }
}

window.__ihAuth = {
  refreshSession,
  scheduleTokenRefresh,
  token,
  refreshToken,
  expiresAt: () => Number(localStorage.getItem(TOKEN_EXPIRES_KEY))
};

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible' || !isLoggedIn()) return;
  const expiresAt = Number(localStorage.getItem(TOKEN_EXPIRES_KEY));
  if (expiresAt && expiresAt - Date.now() < TOKEN_REFRESH_LEAD_MS) {
    void refreshSession();
  }
});
