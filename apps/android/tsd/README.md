# FlowStock TSD (PWA)

## Что это
TSD работает только онлайн через `FlowStock.Server`. Старый оффлайн-поток с `JSONL`, `FlowStock_TSD_DATA.json` и импортом в IndexedDB больше не используется.

## Установка PWA (Android Chrome)
1. Откройте `index.html` по HTTPS.
2. В Chrome нажмите меню ⋮ → `Установить приложение` или `Добавить на главный экран`.
3. Запустите приложение с иконки на домашнем экране.

> Для установки и Service Worker нужен HTTPS или `localhost`.

## Обновление PWA после деплоя
- Переустанавливать PWA после каждого деплоя **не нужно**.
- При изменении `app.js`, `service-worker.js`, `index.html` и других shell-файлов увеличьте версию одновременно в `app-version.js` (`TSD_PWA_VERSION`) и в marker `TSD_SERVICE_WORKER_VERSION` верхнеуровневого `service-worker.js`. Оба файла должны изменяться при каждом shell bump: marker также меняет versioned URL импорта `app-version.js`, чтобы старый WebView обнаружил новый service worker.
- После bump запустите `node apps/android/tsd/service-worker.version.test.js`: тест читает реальные файлы и проверяет совпадение версий, versioned `importScripts` и имя cache `flowstock-tsd-v<version>`.
- После деплоя открытое приложение при следующем запуске или возврате на вкладку проверит `service-worker.js` и предложит обновление.
- Если оператор держит приложение открытым, внизу экрана появится баннер **«Доступна новая версия приложения»** с кнопкой **«Обновить»**.
- Принудительной перезагрузки посреди наполнения/сканирования нет: обновление только по кнопке, и не запускается, пока активна операция на экране наполнения, отгрузки или черновика документа.
- В **Настройках** (шестерёнка) есть кнопка **«Проверить обновления»** — принудительно запрашивает новую версию с сервера, если автоматический баннер не появился.
- В настройках отображается **«Версия приложения: N»** из `app-version.js`.

## Запуск сервера
1. На ПК запустите:
   `dotnet run --project apps/windows/FlowStock.Server --launch-profile https`
2. Откройте в браузере:
   `https://<ip-вашего-пк>:7154/tsd/`
3. Если браузер ругается на dev-сертификат:
   `dotnet dev-certs https --trust`

## Работа на устройстве
- На устройстве должен открываться именно серверный `index.html`.
- PWA использует API сервера и не хранит справочники как оффлайн-датасет.
- Настройки приложения сохраняются локально в браузере.

## Ранняя частичная отгрузка

- List/details `/api/tsd/outbound/orders` содержит additive boolean `allow_partial_outbound`; отсутствующее или `null` значение клиент трактует как `false`.
- При server-returned `true` TSD показывает метку `Частичная отгрузка разрешена`. Клиент не вычисляет eligibility/readiness и не включает заказ самостоятельно.
- Permission не заменяет существующий `allow_partial=true` в запросе неполного `complete` и не ослабляет server-side scan/status guards.

## Совместимость WebView 51
- Для ATOL Smart.Slim Android 7 / старого Android System WebView 51 используется
  упрощенный CSS-режим `html.tsd-legacy-css`.
- Режим включается capability-based в `compat.js`, без UA-sniffing: если нет
  критичной поддержки CSS (`CSS.supports`, `display:grid`, `clamp()`, `min()`),
  TSD получает компактную legacy-верстку shell/login/home/operations/settings и
  простых overlay.
- Современный UI для UROVO, Chrome и новых Android WebView остается текущим:
  `tsd-legacy-css` там не добавляется.
- Этот режим меняет только presentation CSS. Server/API, native bridge,
  scanner contract, документы, ledger и бизнес-логика не меняются.

## Сканер
- По умолчанию используется `keyboard wedge`.
- `Intent`-сканирование поддерживается только через server-hosted `native-bridge.js` в нативной Android-оболочке `apps/android/tsd-native`.
- В обычном Chrome / PWA нужен режим `Keyboard`.
- В native APK используется vendor broadcast transport: ATOL Broadcast-only profile или UROVO Intent-only DataWedge profile. Keyboard output и Intent output не должны быть включены одновременно для одного приложения, иначе один physical scan может стать двумя scan events.
- `native-bridge.js` подключается до `scanner.js`, но в обычном Chrome / PWA ничего не делает: `window.FlowStockAndroidBridge` создаётся только при User-Agent токене `FlowStockTsdNative/1`.

### Ожидаемый JS-bridge
`window.FlowStockAndroidBridge.subscribeScans(callback)`

`callback` получает объект:
```json
{ "value": "460...", "symbology": "EAN13", "raw": {}, "ts": 1710000000000 }
```

## Native Android PoC
- Модуль: `apps/android/tsd-native`, package/namespace `ru.flowstock.tsd`.
- Оболочка хранит один canonical server root URL, например `https://flowstock.local:7154`; `/api/discovery`, `/api/ping` и `/tsd/` вычисляются из него.
- Первый запуск без сохранённого endpoint открывает native setup screen: ручной ввод HTTPS root URL или UDP discovery.
- UDP discovery v1 использует directed broadcast на `7155/udp` в активной Wi-Fi/Ethernet сети. Android protocol и поведение не меняются: UDP-ответ является только подсказкой; сохранить сервер можно только после strict HTTPS validation `/api/discovery`, `/api/ping` и `/tsd/`.
- В production Docker Compose серверный broadcast transport проходит через host-network `discovery-relay`, который принимает `7155/udp` на host и пересылает запрос в `FlowStock.Server` через loopback backend.
- Долгое нажатие аппаратной Back открывает native confirmation смены сервера. В setup смены сервера scanner dispatch останавливается, скрытый WebView не получает сканы, а кнопка `Вернуться к текущему серверу` восстанавливает прежнюю session без очистки cookies/WebStorage. На первом setup без сохранённого endpoint возврат недоступен.
- Короткое Back в рабочем WebView сохраняет существующий контракт: JS bridge → WebView history → `moveTaskToBack(true)`.
- `index.html` внутри WebView не перехватывается и не переписывается. Вся бизнес-логика, API, ledger, документы, остатки, резервы, production quantities и HU readiness остаются на сервере.
- Cleartext запрещён. SSL errors в WebView отменяются; debug-сборка может доверять только публичному dev CA из debug resource overlay. Release APK доверяет system + user trust anchors; FlowStock root CA устанавливается администратором/MDM в Android user trust store. Закрытые ключи не добавлять в репозиторий или APK.
- Локально нужен `apps/android/tsd-native/local.properties` с `sdk.dir=D:\\Android\\SDK`; файл не коммитится.
- Для Gradle на этой машине можно использовать Android Studio JBR и ASCII Gradle cache:
  `cd apps/android/tsd-native`
  `$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"`
  `$env:GRADLE_USER_HOME = "D:\FlowStock\.tmp\gradle-home"`
  `$env:GRADLE_OPTS = "-Dorg.gradle.workers.max=1"`
  `.\gradlew.bat --no-daemon --no-parallel testDebugUnitTest assembleDebug`
- APK после сборки: `apps/android/tsd-native/app/build/outputs/apk/debug/app-debug.apk`.

## ATOL Smart.Slim PoC
- Barcode Service: package `ru.atol.barcodeservice`, version `1.6.3.257`.
- Broadcast action: `com.xcheng.scanner.action.BARCODE_DECODING_BROADCAST`.
- Barcode extra: `EXTRA_BARCODE_DECODING_DATA`; symbology extra: `EXTRA_BARCODE_DECODING_SYMBOLE`.
- Текущий `default`-профиль с `barcodeSendMode = 1` (`Keyboard`) не менять.
- После установки APK создать отдельный FlowStock-профиль через UI Barcode Service, привязать к `ru.flowstock.tsd`, если UI это позволяет, выбрать Broadcast-only, оставить prefix/suffix и дополнительные клавиши пустыми.
- После настройки экспортировать `BarcodeServiceSettings.xml`, проверить фактический mode/action/extras и убедиться, что Keyboard не создаёт второй scan event.
- Rollback выполняется вручную через UI Barcode Service: вернуть `default`/Keyboard или удалить привязку FlowStock-профиля.
- Native diagnostics содержат только длину, hash, masked value, symbology и timestamp; полный barcode не пишется в Logcat, файлы, SharedPreferences или analytics.
- Scanner diagnostics в PWA могут сохранять raw test values в IndexedDB; физическую diagnostic matrix проводите только на выделенных тестовых кодах, не на производственных barcode/КМ.

## UROVO CT48 native APK
- DataWedge service: `com.ubx.datawedge/.service.DataWedgeService`.
- Поддерживаемый Broadcast contract: action `android.intent.ACTION_DECODE_DATA`.
- Barcode string extra: `barcode_string`.
- Raw extra name: `barcode`; native APK проверяет только наличие этого extra и не читает, не преобразует и не сохраняет его содержимое.
- Symbology в подтвержденном UROVO contract отсутствует, поэтому JS bridge получает `symbology: null`.
- После установки совместимого native APK переключите профиль UROVO с Keyboard на Intent-only: delivery `Broadcast`, action `android.intent.ACTION_DECODE_DATA`, string extra `barcode_string`, raw extra `barcode`, Keyboard output выключен.
- Для Chrome/PWA UROVO Intent-only не является целевым режимом. Rollback: вернуть профиль UROVO в Keyboard mode; тогда обычная PWA снова работает через keyboard provider.
- Перед физическим smoke используйте подписанный release APK. Secrets/keystore хранятся вне репозитория; пароли не выводить в консоль и не записывать в Git.
- Проверка release APK:
  `cd apps/android/tsd-native`
  `.\gradlew.bat --no-daemon --no-parallel assembleRelease`
  `apksigner verify --verbose --print-certs <release-apk>`
  Ожидаемый SHA-256 сертификата: `4C:27:68:48:DB:11:21:1D:D5:3F:55:09:78:82:6B:59:53:57:B4:EC:F1:D6:7D:91:2A:09:2B:32:90:55:D9:63`.

## Android 7 матрица
- APK устанавливается на ATOL Smart.Slim Android 7 / API 24 и открывает локальный HTTPS TSD origin.
- `window.FlowStockAndroidBridge` существует только в native UA, а обычная PWA продолжает выбирать keyboard provider.
- Provider в native shell становится `intent`; EAN, QR и GS1 DataMatrix проходят ровно один раз на физический scan.
- GS `0x1D` сохраняется в payload; быстрые разные scans не теряются; scan до готовности bridge отклоняется без replay.
- После reload readiness определяется заново; pause/resume и lock/unlock не создают второй receiver.
- Hardware Back сначала отдаётся web bridge, затем реальной WebView history, затем `moveTaskToBack(true)`.
- На проверенном WebView `com.android.webview 51.0.2704.91` вызов native `ServiceWorkerController` может быть недоступен; оболочка фиксирует это как technical status и продолжает online WebView load без отключения server-hosted Service Worker.

## UROVO CT48 Android 12 ручная smoke-матрица
- Статус до физической проверки: не проверено. Release APK для этой матрицы должен быть сначала собран, подписан и проверен через `apksigner verify --verbose --print-certs`.
- После успешной проверки подписанного release APK установите его на UROVO CT48 Android 12 / SDK 31 и откройте локальный HTTPS TSD origin.
- DataWedge профиль должен быть в Intent-only режиме: Keyboard output выключен, Broadcast action `android.intent.ACTION_DECODE_DATA`, string extra `barcode_string`.
- Ожидаемые проверки: EAN, QR и GS1 DataMatrix; один physical scan -> ровно одно scan event.
- Ожидаемые проверки: GS `0x1D` сохраняется в payload; быстрые разные scans не теряются; одинаковые последовательные scans не отбрасываются native transport как дубликаты.
- Ожидаемые проверки: reload, pause/resume, свернуть/вернуть приложение и lock/unlock не создают второй receiver и не дают дубли scan events.
- После успешного физического smoke этот раздел можно обновить подтвержденным результатом с фактической моделью устройства, Android/WebView версиями и итогом проверок.

## Операции
- На главном экране выберите тип операции.
- Нажмите `+ Новый`, заполните шапку и сканируйте штрихкоды.
- Enter добавляет строку с текущим шагом, Undo отменяет последний скан.
- После заполнения обязательных полей нажмите `Завершить`.
- Для списаний обязательно укажите причину.
- Если штрихкод не найден в каталоге, появится запрос на создание товара.

### Частичное наполнение mixed HU
- В preview mixed HU незаполненные компоненты выбираются checkbox, завершённые компоненты disabled.
- Подтверждение части состава сохраняет component progress без PRD/ledger и показывает прогресс вида `1 / 3`.
- После подтверждения последнего компонента сервер атомарно закрывает dedicated PRD и создаёт складской выпуск по всему составу.
- Component flow доступен только при включённом `ProductionAutoCloseOnFill`; partial component progress не является finished goods stock.

## Контрагенты и локации
- Справочники выбираются через кнопку `Выбрать...`.
- Поиск работает по названию и коду.

## Заказы
- На главном экране есть раздел `Заказы`.
- Поиск работает по номеру заказа и контрагенту.
- Список заказов — **единый**: активные, готовые и выполненные заказы, возвращаемые стандартным
  серверным списком, показываются единым списком без отдельных кнопок `Показать готовые` /
  `Показать выполненные`. Порядок задаёт сервер (активные заказы выше, выполненные ниже).
- Карточка заказа в списке показывает только: номер, тип (`Клиентский` / `Внутренний`),
  контрагента, плановую дату и основной серверный статус. Статус берётся из серверного
  `status_display` (включая `Частично отгружено`); TSD не вычисляет бизнес-статус сам.
- Детали наполнения, паллет и HU доступны после открытия заказа (read-only) и в разделе
  `Наполнение`, а не в карточке списка.

## Состояние склада
- Раздел `Состояние склада` показывает данные с сервера.
- Сканирование остается отдельным сценарием.
- Ручной поиск в этом разделе больше не используется: вместо него доступны фильтры по названию, месту хранения, типу и HU.

## Примечание
- Этот документ и остальные Markdown-файлы репозитория поддерживаются на русском языке.
