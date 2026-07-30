/**
 * TSD PWA shell cache version. Bump on each deploy when shell files change:
 * index.html, service-worker.js, compat.js, app.js, styles.css, storage.js or scanner.js.
 */
(function (root) {
  var version = "72";
  var cacheName = "flowstock-tsd-v" + version;
  root.TSD_PWA_VERSION = version;
  root.TSD_CACHE_NAME = cacheName;
})(typeof self !== "undefined" ? self : window);
