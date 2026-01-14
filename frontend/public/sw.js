self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open("tsd-shell-v1").then((cache) => cache.addAll(["/", "/index.html"]))
  );
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => !k.startsWith("tsd-shell")).map((k) => caches.delete(k)))
    )
  );
  self.clients.claim();
});

self.addEventListener("fetch", (event) => {
  if (event.request.method !== "GET") return;
  event.respondWith(
    caches.match(event.request).then((resp) => resp || fetch(event.request).catch(() => caches.match("/")))
  );
});
