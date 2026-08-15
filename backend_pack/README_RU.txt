BLACKLINK EXTERNAL SEARCH — ГОТОВЫЙ КОМПЛЕКТ BACKEND'ОВ

Состав:
- slskd — Soulseek поиск и загрузка через локальный REST API 127.0.0.1:5030.
- Prowlarr — Torznab-источники для BitTorrent, локальный API 127.0.0.1:9696.
- qBittorrent — передача торрент-загрузок из BlackLink, Web API 127.0.0.1:8080.
- aMule + amuleapi — eD2k/Kad поиск и загрузка, REST API 127.0.0.1:4713.

Все служебные HTTP API привязаны только к 127.0.0.1. Конфиги и данные изолированы внутри папки комплекта.

ПЕРВЫЙ ЗАПУСК
1. Распакуйте ZIP в постоянную папку. После настройки папку лучше не переносить: в конфиге aMule сохраняется абсолютный путь к amuleapi.exe.
2. Запустите SETUP_AND_START.cmd.
3. Введите логин и пароль Soulseek. Скрипт сам создаст read/write API key для slskd; вручную его переносить в BlackLink не нужно.
4. Когда будет запрошена папка BlackLink, укажите папку, где находится blacklink_x64.exe. Можно оставить пустой путь — готовый ExternalSearch.xml останется в BlackLink_Settings.
5. После запуска откроется Prowlarr. Один раз добавьте нужные торрент-индексаторы через его интерфейс.
6. Запустите SYNC_PROWLARR.cmd. Он прочитает текущий API key Prowlarr, создаст отдельный Torznab URL для каждого включённого индексатора и обновит ExternalSearch.xml BlackLink.

ПОСЛЕДУЮЩИЕ ЗАПУСКИ
- START_ALL.cmd — запустить все backend'ы.
- STOP_ALL.cmd — остановить только процессы, запущенные из этой папки.
- STATUS.cmd — проверить локальные API-порты.
- SYNC_PROWLARR.cmd — повторить после добавления/удаления индексаторов Prowlarr.

ЧТО НАСТРАИВАЕТСЯ АВТОМАТИЧЕСКИ
slskd:
- app-dir = Data\slskd
- HTTP = 127.0.0.1:5030
- HTTPS отключён для локального API
- отдельный случайный API key с ролью readwrite
- Soulseek listen port = 50300

qBittorrent:
- portable profile рядом с qbittorrent.exe
- WebUI = 127.0.0.1:8080
- localhost WebUI authentication bypass включён только для локального интерфейса
- CSRF, Host Header Validation и Clickjacking Protection оставлены включёнными
- загрузки по умолчанию: Downloads\BitTorrent

Prowlarr:
- data dir = Data\Prowlarr
- bind = 127.0.0.1
- port = 9696
- браузер при обычном фоновом старте отключён
- API key генерируется при первом setup

аMule:
- config dir = Data\aMule
- amuleapi включён
- bind = 127.0.0.1
- HTTP port = 4713
- случайный admin password создаётся официальной командой amuleapi --set-admin-pass
- тот же пароль автоматически записывается в BlackLink ExternalSearch.xml

ФАЙЛЫ
- Data\bundle-secrets.json — автоматически созданные локальные секреты backend'ов. Не публикуйте этот файл.
- BlackLink_Settings\ExternalSearch.xml — готовая конфигурация BlackLink.
- Data\BlackLinkPath.txt — путь к BlackLink, если он был указан при setup.

ВАЖНО
Prowlarr не может сам решить, какими индексаторами вы хотите пользоваться: часть индексаторов требует учётную запись, cookie, passkey или имеет собственные правила. Поэтому добавление индексаторов — единственный обязательный ручной шаг BitTorrent-поиска. Всё соединение Prowlarr -> BlackLink после этого делает SYNC_PROWLARR.cmd.

Soulseek также требует ваши собственные сетевые учётные данные; скрипт не создаёт фиктивный логин.

Не открывайте порты 5030, 9696, 8080 и 4713 наружу и не перенаправляйте их на роутере. Это локальные управляющие API.
