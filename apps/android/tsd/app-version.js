/**
 * Версия shell-кэша TSD PWA. Увеличивайте при каждом деплое, если менялись
 * index.html, service-worker.js, app.js, styles.css, storage.js или scanner.js.
 */
(function (root) {
  var version = "15";
  var cacheName = "flowstock-tsd-v" + version;
  root.TSD_PWA_VERSION = version;
  root.TSD_CACHE_NAME = cacheName;
})(typeof self !== "undefined" ? self : window);
