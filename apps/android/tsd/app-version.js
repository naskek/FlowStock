/**
 * Р’РµСЂСЃРёСЏ shell-РєСЌС€Р° TSD PWA. РЈРІРµР»РёС‡РёРІР°Р№С‚Рµ РїСЂРё РєР°Р¶РґРѕРј РґРµРїР»РѕРµ, РµСЃР»Рё РјРµРЅСЏР»РёСЃСЊ
 * index.html, service-worker.js, app.js, styles.css, storage.js РёР»Рё scanner.js.
 */
(function (root) {
  var version = "16";
  var cacheName = "flowstock-tsd-v" + version;
  root.TSD_PWA_VERSION = version;
  root.TSD_CACHE_NAME = cacheName;
})(typeof self !== "undefined" ? self : window);
