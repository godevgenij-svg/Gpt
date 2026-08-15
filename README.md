# GreyLink CI

Сборочный репозиторий проекта GreyLink / BlackLink-Grey.

Назначение: воспроизводимая Windows x64 сборка модифицированного BlackLink через GitHub Actions.

База upstream: `zipper9/blacklink` commit `1a72cfddca154da9070caca1b5a02df56d5498ab`.

Текущий этап: GreyBridge Stage 2 (DC/NMDC/ADC + Soulseek/slskd + Torznab + qBittorrent integration).

Исходный код BlackLink не дублируется в этом репозитории: CI получает точную upstream-версию и применяет патч GreyLink. Это уменьшает репозиторий и позволяет однозначно воспроизводить сборку.
