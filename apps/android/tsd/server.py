from __future__ import annotations

import http.server
import logging
import socketserver
import sys
from pathlib import Path

PORT = 8080
BIND = "0.0.0.0"
ROOT = Path(__file__).resolve().parent  # папка tsd


class LoggingHandler(http.server.SimpleHTTPRequestHandler):
    # Раздаём файлы именно из папки ROOT (Python 3.7+ поддерживает directory=)
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(ROOT), **kwargs)

    def log_message(self, format: str, *args) -> None:
        # Стандартный access-log http.server перенаправляем в logging
        logging.info('%s - - [%s] %s',
                     self.client_address[0],
                     self.log_date_time_string(),
                     format % args)

    def log_error(self, format: str, *args) -> None:
        logging.error('%s - - [%s] %s',
                      self.client_address[0],
                      self.log_date_time_string(),
                      format % args)

    def send_error(self, code, message=None, explain=None):
        # Логируем ошибки HTTP (404/500 и т.д.) более явно
        logging.warning("HTTP error %s for %s %s from %s",
                        code, self.command, self.path, self.client_address[0])
        return super().send_error(code, message, explain)


class ThreadingTCPServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    daemon_threads = True
    allow_reuse_address = True


def main() -> int:
    log_file = ROOT / "server.log"

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        handlers=[
            logging.StreamHandler(sys.stdout),
            logging.FileHandler(log_file, encoding="utf-8"),
        ],
    )

    logging.info("LightWMS TSD HTTP Server starting")
    logging.info("Root: %s", ROOT)
    logging.info("Bind: %s", BIND)
    logging.info("Port: %s", PORT)
    logging.info("Log file: %s", log_file)
    logging.info("Press Ctrl+C to stop.")

    try:
        with ThreadingTCPServer((BIND, PORT), LoggingHandler) as httpd:
            httpd.serve_forever()
    except OSError as e:
        logging.exception("Failed to start server (port busy? firewall?): %s", e)
        return 2
    except KeyboardInterrupt:
        logging.info("Stopping by Ctrl+C.")
        return 0
    except Exception as e:
        logging.exception("Unhandled server error: %s", e)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
