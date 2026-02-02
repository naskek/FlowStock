from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import ssl
import os
import sys
import mimetypes

HOST = "0.0.0.0"
PORT = 8443

CERT_FILE = r"C:\Users\ЧестныйЗнак\10.1.30.53+1.pem"
KEY_FILE  = r"C:\Users\ЧестныйЗнак\10.1.30.53+1-key.pem"

ROOT_DIR = os.path.dirname(os.path.abspath(__file__))

# на всякий случай, чтобы корректно отдавать js/json
mimetypes.add_type("application/javascript", ".js")
mimetypes.add_type("application/json", ".json")
mimetypes.add_type("text/css", ".css")

class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT_DIR, **kwargs)

    def end_headers(self):
        path = self.path.split("?", 1)[0]
        # чтобы SW/manifest не залипали в кеше при обновлениях
        if path.endswith("/service-worker.js") or path.endswith("/manifest.json"):
            self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
            self.send_header("Pragma", "no-cache")
            self.send_header("Expires", "0")
        else:
            self.send_header("Cache-Control", "no-cache")
        super().end_headers()

def main():
    if not os.path.exists(CERT_FILE):
        print(f"ERROR: cert not found: {CERT_FILE}")
        sys.exit(1)
    if not os.path.exists(KEY_FILE):
        print(f"ERROR: key not found: {KEY_FILE}")
        sys.exit(1)

    httpd = ThreadingHTTPServer((HOST, PORT), Handler)

    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(certfile=CERT_FILE, keyfile=KEY_FILE)
    httpd.socket = ctx.wrap_socket(httpd.socket, server_side=True)

    print(f"HTTPS server: https://{HOST}:{PORT}/ (root: {ROOT_DIR})")
    print(f"Open on PC:   https://localhost:{PORT}/index.html")
    print(f"Open on TSD:  https://10.1.30.53:{PORT}/index.html")
    httpd.serve_forever()

if __name__ == "__main__":
    main()
