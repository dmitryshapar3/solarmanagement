# Production readiness и SOLID: аудит 5 октября 2026

Аудит выполнен для объединённого remote main и всей дополнительной запушенной работы в ветке подписок. Логирование как отдельная система и масштабирование исключены по запросу. Проверялись сервер, SQL Server, мобильное приложение, StoreKit, runtime интеграций, десять провайдеров, миграции, CI и восстановление данных.

Система стала существенно устойчивее, но статус «готово к реальным платежам и промышленным устройствам без оставшихся условий» пока преждевременен. Исправленные дефекты перечислены ниже; отдельно указаны условия, требующие реальной инфраструктуры, внешних аккаунтов или продуктового решения. SOLID здесь проверяется по обязанностям, зависимостям и доступным контрактам, а не количеством классов. Абсолютное отсутствие нарушений во всех возможных будущих сценариях тесты не доказывают.

## Исправленные cross-cutting проблемы

| Область | Риск до исправления | Результат |
| --- | --- | --- |
| Сессии | Bearer-сессии пропадали при перезапуске; отзыв доступа мог не затронуть открытый circuit | Persistent SQL session store, SHA-256 вместо хранения bearer-токена, лимит сессий на пользователя, проверка security stamp, lockout, membership и роли при обращении |
| Граница account / installation | Удаление membership могло лишить пользователя возможности управлять аккаунтом; сессия могла следовать за новой установкой | Account/billing остаются доступны; private tenant API возвращает 403; installation binding сессии неизменяем, новый tenant требует нового входа |
| Необратимые команды | Разрешение могло устареть во время ожидания очереди или SQL-блокировки | Повторная авторизация после ожидания и перед dispatch; v2 API использует ту же границу; rejected intent не отправляется провайдеру |
| Снятие uncertain | Старая телеметрия или отозванная сессия могли разрешить закрыть блокирующий intent | Нужна свежая физическая online-observation; доступ и срок observation проверяются после ожиданий; неизвестное состояние продолжает блокировать устройство |
| Удаление аккаунта | Прерывание между закрытием admission и удалением могло навсегда отключить установку | Fresh proof, проверка shared ownership и unresolved commands, pause/drain runtime, транзакционный fence, компенсация отмены и startup recovery записанных offboarding fences |
| Защита мобильного аккаунта | Поздний ответ старой операции мог затронуть сменившийся аккаунт | Account-generation fence до destructive request и перед logout; fresh password/OTP; отдельные смена пароля, отзыв сессий, export и deletion |
| Ошибки API | Необработанные ошибки давали HTML или раскрывали детали реализации | Безопасный JSON problem response с code/status/traceId, 409/402/503 и Retry-After; отмена клиентом не превращается в 500; API body ограничен 1 MiB |
| Клиентский контракт | Успешный HTTP с неверным JSON мог стать ошибкой состояния/доступа | Проверка runtime DTO перед применением; React/Blazor error boundaries; переводы новых пользовательских сообщений во всех 15 каталогах |
| Конфигурация | Несколько связанных секций могли сохраниться частично | Batch-save одним SQL commit, запрет изменения operator/security sections, повторная загрузка после commit; fault-injection проверяет отсутствие частичного сохранения |
| Правила | Смена цели сохраняла старое runtime state; параллельное редактирование перетирало изменения; два правила могли конкурировать за устройство | Reset при смене цели, канонический запрет конфликтующих enabled rules, config-version conflict, повторная проверка времени решения непосредственно перед dispatch |
| Наблюдения | Неизвестное состояние выглядело как OFF | Явное stateKnown/Unknown в DTO и UI; uncertain/pending не повторяется как новая команда |
| Провайдеры | Повтор транспорта и базовых классов, неразличимые ошибки, небезопасный retry | Общий bounded transport и worker SDK, типизированная классификация ошибок; retry только чтений; неопределённые команды автоматически не повторяются |
| Пакеты | Изменённый payload мог получить уже опубликованный immutable version | Deye/Shelly 1.0.2, остальные восемь 1.0.1; старые pins сохраняются; legacy bootstrap понимает предыдущие совместимые версии |
| Apple billing | Сервис смешивал хранение, policy и transport; зависший body-read мог задерживать refresh | Чистая Domain policy, раздельные catalog/writer, bounded request/body deadline, paged refresh и monotonic SQL observation fences |
| Startup / миграции | Runtime мог менять schema привилегированной учёткой и работать при drift | Отдельный migrate-only job, Production validate-only, проверка schema/истории и ограниченных SQL прав; runtime контейнера работает без root |
| Readiness | Процесс выглядел живым при SQL/worker/storage отказе | Раздельные live/ready, heartbeat workers, bounded SQL check; health пути независимы от authentication и billing |
| Reverse proxy / браузер | Автоматическое доверие forwarded headers и отсутствие общей защиты ответа | Только configured trusted proxy, X-Forwarded-For/Proto; nosniff, same-origin referrer, запрет framing/object, HSTS при HTTPS вне Development |
| Backup / restore | SQL, ключи и подписанные пакеты могли восстановиться в несовместимых состояниях | Coordinated encrypted authenticated backup с остановкой writers; проверка до mutation; fresh SQL/volumes restore; сохраняются sessions, billing, approvals, keys и pins |
| Retention | Истёкшие сессии и OAuth flows накапливались | Индексированные ограниченные batch cleanup с deadline и worker health; durable command journal намеренно сохраняется для предотвращения replay |
| Зависимости | Уязвимые NuGet/npm версии и невоспроизводимый restore | NuGet lock files во всех проектах, locked restore и audit gate; опубликованные npm исправления применены; два оставшихся upstream исключения ограничены датой и проверкой отсутствия в runtime bundle |
| Native CI | Нестабильный StoreKit runtime, неполное подтверждение discovery, checkout path в CocoaPods checksum | StoreKit и полный Expo build разделены; фиксированный runtime; обязательны 11 ожидаемых тестов без skips; переносимый checksum и 16 Ruby регрессий |

## Разделение обязанностей и зависимостей

- `DeyeSolar.Domain/Billing` содержит account/subscription entities, product policy и расчёт entitlement. Domain не зависит от ASP.NET, SQL, Apple transport или Web configuration.
- `BillingAccessService`, `TrialSocketQuota`, `SqlAppleSubscriptionStore`, `AppleAppStoreClient` и refresh worker отвечают за разные задачи. Read/catalog/write/control представлены отдельными интерфейсами.
- `AccountSecurityService` координирует fresh proof, export и deletion, вынесенные в самостоятельные сервисы. Session storage и installation authorization имеют свои контракты.
- Gateway остаётся facade над inventory, observation, binding guard и command lifecycle. Настройка интеграций использует отдельные access, connection lifecycle, configuration resolver/writer, selection token и device binding ports.
- Interactive rule consumers используют `IConfigurationRules`; worker bookkeeping остаётся в `IRuleRepository`. Интерактивный адаптер больше не обязан реализовывать недоступную операцию.
- Polling cycle разделён на automation executor, observation reconciler, run history и receipt reconciler. Composition root связывает конкретные реализации; потребители работают через узкие порты.
- `Program.cs` оставлен для composition/startup; operator preflight, schema initialization, seed, pipeline и health вынесены отдельно.
- Мобильный SubscriptionContext адаптирует React/native events к SubscriptionController; receipt synchronization имеет самостоятельную ответственность и account-bound acknowledgement.
- Read-only settings consumers и label adapter используют `IAppSettingsReader`; mutations используют отдельный `IAppSettingsWriter`, включая Razor/Mobile API и production DI.
- DI применяется к внешним зависимостям и меняющимся стратегиям: clock, storage, transport, authorization, lifecycle. Чистые функции и value objects не требуют искусственных service interfaces.

## Что ещё нужно до реального production rollout

| Приоритет | Условие | Как закрыть |
| --- | --- | --- |
| P1 | Две upstream npm уязвимости без исправленной версии: braces 3.0.3 и node-forge 1.4.0 | Обновить после upstream release; до этого build принимает только trusted repository/build input. CI разрешает только точные advisory/version, проверяет отсутствие этих модулей в iOS source map и блокирует исключения с 4 ноября 2026 |
| P1 | .NET 8 заканчивает поддержку 10 ноября 2026 | Запланировать и отдельно проверить переход на .NET 10 LTS / согласованные EF и Identity версии до этой даты; сейчас установлен последний поддержанный .NET 8 patch |
| P1 | Нет подтверждения работы с реальными внешними credentials и устройствами | Пройти staging acceptance для Google redirect/origins, email/SMS delivery, Apple sandbox/App Store products и всех используемых производителей; local StoreKit и fixture provider E2E не заменяют live acceptance |
| P1 | Защита секретов и key volumes зависит от deployment | Выдать secrets вне Git/image, ограничить volume/host access, включить encryption at rest средствами хоста/хранилища, определить владельца и порядок ротации ключей. Persistent key files сами по себе не подтверждают шифрование production диска |
| P1 | Recovery script ещё не означает эксплуатационный DR | Назначить RPO/RTO, расписание, off-host storage, retention и владельца восстановления; повторить drill на production-sized SQL и точных опубликованных архивных пакетах |
| P1 | Нужна проверка настоящей среды deploy | Проверить TLS, certificate renewal, DNS, trusted proxy, persistent volumes, storage permissions и migrate/validate rollout с реально выбранными secrets и image digest. Kubernetes manifest проверен статически; реальный кластер в этом аудите не разворачивался |
| P2 | Product policy для уже включённых устройств при окончании entitlement | Утвердить ожидаемое физическое состояние и уведомление пользователя. Текущая политика запрещает новую автоматическую команду без entitlement; OFF при недоступном инверторе разрешается только пока entitlement действителен |
| P2 | Legacy rule clients ещё могут писать без config-version token | Современные клиенты передают token и получают 409 при конфликте; после периода совместимости сделать precondition обязательным и определить API version/deprecation policy |
| P2 | Неограниченная durable история команд/config revisions/key history | Определить безопасный retention/compaction с сохранением idempotency tombstones и расшифровки оставшихся secrets. Простое удаление старых команд возобновит replay и поэтому не добавлено |
| P2 | Обратимые configuration writes считаются допущенными после первоначальной проверки доступа | Отзыв запрещает последующие действия, но уже допущенная операция может завершить SQL commit. Если нужен строгий запрет любой записи после revoke, требуется единый caller-aware транзакционный fence для membership и configuration mutations |
| P2 | Нет доказательства поведения на реальных нагрузках и длительном soak | Установить latency/memory/queue/storage budgets и прогнать длительный single-instance soak с outage/reconnect/clock scenarios. Это проверка надёжности одного процесса, а не отложенное масштабирование |
| P2 | Эксплуатационные и пользовательские процессы | Определить owner поддержки, incident escalation, account recovery, data-retention/delete/export policy и release rollback через проверенный restore. Логирование как система остаётся вне этого запроса |

Безопасный rollback schema для новых session/offboarding миграций намеренно запрещён до любых изменений данных: SQL THROW 51000 сохраняет migration history и данные. Для отката релиза требуется согласованный restore backup или forward fix, а не попытка автоматически удалить persistent security state.

## Проверки и границы доказательства

Итоговый полный прогон: **1219/1219 .NET-тестов**, все пять test projects найдены по TRX, failures/skips отсутствуют. Web — 754, Infrastructure — 263, RuleEngine — 21, Integration Runtime — 93, Provider E2E — 88. Дополнительно: **mobile 231/231**, **StoreKit 11/11**, **CocoaPods checksum 16/16**, исправленные серверные регрессии 40/40, TypeScript и 15 языковых каталогов / 1677 фраз. Все SQL факты запускались с `SOLAR_TEST_SQL_CONNECTION` на настоящем SQL Server 2022 в Docker; fixture проверки не пропускались. Locked NuGet restore и direct/transitive audit: 0 известных уязвимостей. npm audit: 16 affected dependency paths, сводящихся к двум описанным upstream exceptions; их отсутствие в итоговом iOS runtime bundle проверено source map.

Production Docker drill уже подтвердил: signed bundle проверяется до SQL mutation; старый SQL package pin и trial/account token сохраняются при обновлении catalog; runtime работает с DML-only SQL user и nonroot UID; bearer переживает restart; SQL outage даёт ready=503 менее чем за 7 секунд при live=200; authenticated encrypted restore в новую БД и volumes сохраняет billing fingerprint, session, keys и pins. Старые версии для этой проверки создаются как явно обозначенные подписанные fixtures из текущего payload: совместимость реальных исторических бинарных архивов требует отдельного drill с exact release artifacts.

Provider E2E использует настоящие подписанные workers/runtime и контролируемые ответы внешних API. Нативные тесты используют настоящий StoreKitTest, симулятор и подписанный test host. Полный iOS Release simulator build проверяет и JavaScript bundle, и настоящий Expo Swift bridge; это не device archive/TestFlight upload.

Production Docker image итогового drill: `sha256:d80c6bbe40f6bdd5dadcf29241c6ec7291fd88de4f8b5fc28dea2fde1a349d71`, runtime `USER app`. Runtime .NET 8.0.31; локальный SDK 10.0.301, CI использует актуальный SDK 8. SQL Server 2022 выполнялся в Docker.

Сборка Release не содержит ошибок. Существующие Blazor render-tree test harnesses дают предупреждения BL0006 о зависимости от внутренних типов framework; production source не использует эти тестовые adapters. При переходе на новый .NET потребуется проверить/заменить эти harnesses; предупреждения не подавлялись.

### Источники для оставшихся dependency/lifecycle условий

- [braces advisory](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm)
- [node-forge advisory](https://github.com/advisories/GHSA-86w9-cpqp-85rv)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [GitHub macOS 15 runner tools](https://github.com/actions/runner-images/blob/main/images/macos/macos-15-Readme.md)
- [Практический runbook проекта](production-operations.md)
