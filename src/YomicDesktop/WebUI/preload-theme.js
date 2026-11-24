(function () {
  try {
    var preload = window.__YOMIC_PRELOAD__;
    var isDark = preload && preload.settings && typeof preload.settings.isDarkMode === 'boolean'
      ? preload.settings.isDarkMode
      : null;
    if (isDark === null) {
      var cached = localStorage.getItem('yomic_bootstrap_cache');
      if (cached) {
        var parsed = JSON.parse(cached);
        if (parsed && parsed.settings && typeof parsed.settings.isDarkMode === 'boolean') {
          isDark = parsed.settings.isDarkMode;
        }
      }
    }
    if (isDark) {
      document.documentElement.setAttribute('data-theme', 'dark');
    }
    if (localStorage.getItem('yomic_onboarding_completed') !== 'true') {
      document.documentElement.setAttribute('data-onboarding-pending', 'true');
    }
  } catch (e) {}
})();
