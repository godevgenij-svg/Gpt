# GreyVPN v0.1

Windows-каркас VPN-клиента/менеджера для большого набора разнородных конфигураций.

## Уже работает

- импорт отдельных файлов и целых папок;
- OpenVPN `.ovpn`;
- WireGuard `.conf`;
- AmneziaWG `.conf` по расширенным AWG-параметрам;
- списки `vless://`, `vmess://`, `trojan://`, `hysteria2://`, `hy2://`, `ss://` из `.txt`;
- регистрация JSON/YAML/Amnezia `.vpn` как профилей для следующих адаптеров;
- таблица, сортировка, массовое выделение;
- последовательный предварительный тест endpoint без flood;
- TCP-connect для TCP-профилей;
- ping-проверка для UDP/WireGuard/AWG, где TCP-test бессмыслен;
- остановка проверки;
- локальное сохранение базы в `%LocalAppData%\GreyVPN\profiles.json`;
- дедупликация URI без ошибочного объединения разных VPN-ключей на одном endpoint.

## Важно

Статус `TCP доступен` / `Хост доступен` в v0.1 — это **предварительная проверка endpoint**, а не доказательство успешной VPN-авторизации.
Полноценный connect-test появится после подключения engine adapters.

## План следующих этапов

1. OpenVPN adapter: запуск официального OpenVPN engine и чтение management/status.
2. WireGuard adapter: WireGuardNT / wireguard-windows service lifecycle.
3. AmneziaWG adapter: официальный AWG Windows engine.
4. Xray/sing-box adapter для VLESS/VMess/Trojan/Hysteria2.
5. Полный автотест: connect -> внешний IP -> HTTPS -> latency -> disconnect.
6. История, рейтинг, авто-выбор лучшего рабочего профиля.
7. Импорт Amnezia `.backup` и Clash/Mihomo YAML на уровне отдельных узлов.

## Сборка

Требуется .NET 8 SDK на Windows:

```powershell
dotnet publish .\src\GreyVPN\GreyVPN.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```
