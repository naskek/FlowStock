/**
 * TSD PWA shell cache version. Bump on each deploy when shell files change:
 * index.html, service-worker.js, app.js, styles.css, storage.js or scanner.js.
 */
(function (root) {
  var version = "57";
  var cacheName = "flowstock-tsd-v" + version;
  root.TSD_PWA_VERSION = version;
  root.TSD_CACHE_NAME = cacheName;
})(typeof self !== "undefined" ? self : window);
