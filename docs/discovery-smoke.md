# Полная проверка UDP discovery после deploy

Расширенный smoke-тест discovery-протокола. Базовая проверка (`curl /api/discovery`) описана в основном deployment.md.

## Контекст

- Android discovery v1 отправляет directed broadcast в активную Wi-Fi/Ethernet подсеть.
- UDP-ответ является только подсказкой: приложение сохраняет сервер только после strict HTTPS validation `/api/discovery`, `/api/ping` и `/tsd/`.
- Docker Compose публикует `7155:7155/udp` у сервиса `flowstock`; host network не используется.
- `docker compose config -q` проверяет только корректность compose-файла. Он не доказывает, что directed broadcast из LAN проходит через firewall/Docker bridge и что unicast UDP-ответ возвращается клиенту.

## Порядок проверки

1. Убедиться, что DNS `flowstock.local` указывает на сервер, а `FLOWSTOCK_PUBLIC_BASE_URL` совпадает с SAN сертификата.
2. Проверить HTTPS identity:

```bash
curl -fsS https://flowstock.local:7154/api/discovery
```

3. С устройства/ПК в той же Wi-Fi/Ethernet подсети отправить UDP request на directed broadcast `7155/udp` с JSON:

```json
{"product":"FlowStock","discovery_protocol_version":1,"nonce":"0123456789abcdef0123456789abcdef"}
```

4. Убедиться, что ответ пришёл unicast от сервера и содержит тот же nonce и canonical HTTPS URL.
5. Если broadcast через Docker published UDP port не проходит — не включать host network без отдельного архитектурного решения. Сначала проверить firewall, router/AP isolation и возможность минимального production-safe сетевого изменения.
