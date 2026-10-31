# ServerMonitor — پلتفرم مانیتورینگ و Observability سرورها

ServerMonitor یک سامانه‌ی **بدون Agent** و **بلادرنگ** برای مانیتورینگ سرورهای **Linux** و **Windows** است. لازم نیست روی سرورهای مقصد چیزی نصب شود: اطلاعات لینوکس از طریق **SSH** و اطلاعات ویندوز از طریق **WinRM** خوانده می‌شود.

| لایه | فناوری |
|---|---|
| Backend | ASP.NET Core 10، BackgroundService، REST API |
| UI | Blazor Web App (Interactive Server)، Bootstrap 5، Bootstrap Icons، ApexCharts |
| بلادرنگ | SignalR |
| داده | EF Core 10 + PostgreSQL 17 |
| اتصال به سرورها | SSH.NET (لینوکس)، کلاینت داخلی WS-Management (ویندوز) |
| امنیت | ASP.NET Core Identity، Data Protection، Rate Limiting، CSP |
| استقرار | Docker، Docker Compose، Caddy (HTTPS خودکار) |

> همه‌ی داده‌هایی که در UI می‌بینید از سرورهای واقعی جمع‌آوری می‌شوند و هیچ داده‌ی ساختگی (Mock) وجود ندارد. برای آزمایش سریع، پروفایل `demo` یک کانتینر واقعی OpenSSH بالا می‌آورد که می‌توانید آن را مانیتور کنید.

> ⚠️ فقط سرورهایی را مانیتور کنید که مالک یا مدیر مجاز آن‌ها هستید.

---

## فهرست

1. [امکانات](#امکانات)
2. [شروع سریع با Docker](#شروع-سریع-با-docker)
3. [استقرار روی سرور (Production)](#استقرار-روی-سرور-production)
4. [اجرای محلی بدون Docker (توسعه)](#اجرای-محلی-بدون-docker-توسعه)
5. [آماده‌سازی سرورهای مقصد](#آمادهسازی-سرورهای-مقصد)
6. [راهنمای صفحات برنامه](#راهنمای-صفحات-برنامه)
7. [هشدارها و اعلان‌ها](#هشدارها-و-اعلانها)
8. [نقش‌ها و دسترسی‌ها](#نقشها-و-دسترسیها)
9. [معماری](#معماری)
10. [مدل امنیتی](#مدل-امنیتی)
11. [REST API و SignalR](#rest-api-و-signalr)
12. [مرجع تنظیمات](#مرجع-تنظیمات)
13. [پشتیبان‌گیری، بازیابی و به‌روزرسانی](#پشتیبانگیری-بازیابی-و-بهروزرسانی)
14. [عیب‌یابی](#عیبیابی)
15. [محدودیت‌های شناخته‌شده](#محدودیتهای-شناختهشده)
16. [ساختار پروژه](#ساختار-پروژه)

---

## امکانات

| بخش | چه چیزی جمع‌آوری یا ارائه می‌شود |
|---|---|
| **مدیریت سرورها** | افزودن، ویرایش و حذف؛ گروه، تگ، محل استقرار و توضیحات؛ فاصله‌ی پایش جداگانه برای هر سرور؛ فعال یا غیرفعال کردن پایش؛ تست اتصال؛ **Pin کردن کلید میزبان SSH یا گواهی WinRM**؛ ذخیره‌ی رمزنگاری‌شده‌ی رمز عبور یا کلید خصوصی SSH (به همراه passphrase) |
| **CPU** | درصد کل، درصد هر هسته، تعداد هسته‌ها، Load Average در بازه‌های 1، 5 و 15 دقیقه (در ویندوز: Processor Queue)، فرکانس، دما (اگر سخت‌افزار گزارش کند)، تاریخچه |
| **حافظه** | کل، مصرف‌شده، آزاد و در دسترس؛ درصد مصرف؛ Swap یا Page File؛ تاریخچه |
| **دیسک** | برای هر Volume: اندازه، مصرف و درصد؛ سرعت خواندن و نوشتن (بایت بر ثانیه)؛ تأخیر خواندن و نوشتن (ms)؛ تاریخچه |
| **شبکه** | برای هر Interface: وضعیت، سرعت، نرخ ارسال و دریافت، مجموع ترافیک، خطاها و Dropها. همچنین وضعیت‌های TCP، اتصالات Established و پورت‌های Listen |
| **سیستم** | Uptime، تعداد پروسس‌ها و Threadها، File Descriptorهای باز (لینوکس) یا Handleها (ویندوز)، نشست‌های فعال، **لیست زنده‌ی پروسس‌ها** قابل مرتب‌سازی بر اساس CPU و RAM |
| **تأخیر و زمان پاسخ** | سه اندازه‌گیری **مستقل**: **ICMP Ping**، **TCP Connect** و **HTTP(S)**. زمان HTTP به فازهای DNS، TCP Connect، TLS Handshake، TTFB و Total تفکیک می‌شود. آمار Min، Max، Avg، P50، P90، P95، P99، Jitter و Packet Loss برای بازه‌های 5m، 15m، 1h، 6h، 24h، 7d و بازه‌ی دلخواه |
| **سرویس‌ها** | واحدهای systemd و سرویس‌های ویندوز: وضعیت، Sub-state، نوع راه‌اندازی، آخرین تغییر وضعیت، خطاهای اخیر؛ امکان افزودن سرویس دلخواه؛ **Restart** سرویس (فقط اگر برای آن سرور فعال شده باشد و کاربر Operator یا بالاتر باشد) |
| **پورت‌ها** | فقط پورت‌های TCP که مدیر تعریف کرده است (بدون اسکن بازه‌ای): باز یا بسته، زمان پاسخ، آخرین تغییر |
| **امنیت** | ورودهای ناموفق و موفق؛ ورود مشکوک (Brute Force یا کاربران دارای دسترسی بالا)؛ وضعیت فایروال؛ پورت‌های Listen جدید؛ تغییر وضعیت سرویس‌ها؛ به‌روزرسانی‌های (امنیتی) معوق؛ نیاز به Reboot؛ رمزنگاری دیسک (LUKS یا BitLocker)؛ تاریخ انقضا و اعتبار گواهی TLS |
| **لاگ‌ها** | فقط منابع مجاز (لیست سفید)؛ Live Tail؛ جستجو با هایلایت؛ فیلتر سطح و زمان؛ نمای جزئیات؛ خروجی CSV |
| **هشدارها** | قوانین آستانه‌ای سراسری یا مخصوص یک سرور؛ سه سطح Info، Warning و Critical؛ جلوگیری از نوسان با N عبور متوالی؛ **Deduplication**؛ **Cooldown**؛ بسته شدن خودکار؛ Acknowledge و Resolve؛ تاریخچه؛ ارسال به Email، Telegram و Webhook (با امضای HMAC) |
| **نمودارها** | CPU، RAM، Swap، دیسک، شبکه، Disk I/O، Load، TCP، پروسس‌ها، Ping، Packet Loss، TCP Connect و HTTP؛ به‌روزرسانی زنده؛ Zoom و Pan؛ Tooltip؛ Min، Max و Avg؛ مقایسه‌ی چند سرور؛ خروجی CSV و PDF |
| **SLA** | درصد Uptime، مدت Downtime، تعداد Incidentها، MTTR، MTBF، Error Budget، Timeline دسترس‌پذیری؛ گزارش روزانه، هفتگی و ماهانه (CSV و PDF) |
| **کاربران** | نقش‌های Admin، Operator و Viewer؛ قفل حساب پس از تلاش ناموفق؛ خروج اجباری از همه‌ی نشست‌ها؛ Audit Log کامل |
| **API** | REST در مسیر `/api/v1/*` و SignalR Hub در `/hubs/monitoring` |

---

## شروع سریع با Docker

پیش‌نیاز: Docker Engine به همراه Compose plugin.

**۱. ساخت فایل تنظیمات**

```bash
cp .env.example .env
```

حداقل مقدارهای `DB_PASSWORD`، `ADMIN_EMAIL` و `ADMIN_PASSWORD` را در `.env` تغییر دهید. رمز مدیر باید حداقل ۱۲ کاراکتر باشد.

**۲. ساخت و اجرا**

```bash
docker compose up -d --build
```

**۳. ورود**

آدرس `http://SERVER-IP:8080` را باز کنید و با `ADMIN_EMAIL` و `ADMIN_PASSWORD` وارد شوید.

اگر `ADMIN_PASSWORD` را خالی گذاشته باشید، یک رمز تصادفی ساخته و در Volume کلیدها ذخیره می‌شود:

```bash
docker compose exec app cat /app/keys/initial-admin-password.txt
```

بعد از اولین ورود، رمز را از صفحه‌ی **Users** عوض کنید و این فایل را پاک کنید:

```bash
docker compose exec app rm /app/keys/initial-admin-password.txt
```

> کاربر مدیر فقط زمانی ساخته می‌شود که پایگاه داده هیچ کاربری نداشته باشد. پس از آن تغییر `ADMIN_PASSWORD` در `.env` اثری ندارد و رمز را باید از UI عوض کنید.

**۴. افزودن اولین سرور**

1. وارد صفحه‌ی **Servers** شوید و **Add server** را بزنید.
2. نام، آدرس، سیستم‌عامل، پروتکل، نام کاربری و رمز یا کلید را وارد کنید.
3. **Test connection** را بزنید. اثر انگشت کلید SSH (یا Thumbprint گواهی WinRM) نمایش داده می‌شود. آن را با مقدار واقعی روی سرور مقایسه کنید (بخش [آماده‌سازی سرورها](#آمادهسازی-سرورهای-مقصد)) و **Trust** را بزنید.
4. ذخیره کنید. جمع‌آوری اطلاعات بلافاصله شروع می‌شود.

### امتحان با یک سرور لینوکسی آزمایشی

```bash
docker compose --profile demo up -d
```

قبل از اجرا، `DEMO_SSH_PASSWORD` را در `.env` تنظیم کنید. سپس یک سرور Linux با این مشخصات اضافه کنید: Host برابر `demo-target`، Port برابر `2222`، User برابر `monitor` و همان رمز.

---

## استقرار روی سرور (Production)

### مراحل

1. Docker Engine و Compose plugin را روی سرور نصب کنید.
2. پوشه‌ی پروژه را به سرور کپی کنید، مثلاً در `/opt/servermonitor`. پوشه‌های `bin/`، `obj/` و `.dev-keys/` لازم نیستند.
3. فایل تنظیمات را بسازید و دسترسی آن را محدود کنید:

```bash
cp .env.example .env && chmod 600 .env
```

4. یک رمز قوی برای پایگاه داده بسازید و در `DB_PASSWORD` قرار دهید:

```bash
openssl rand -base64 32
```

5. برنامه را اجرا کنید:

```bash
docker compose up -d --build
```

6. وضعیت را بررسی کنید. سرویس `app` باید `healthy` باشد:

```bash
docker compose ps
```

```bash
docker compose logs -f app
```

### HTTPS با Caddy (پیشنهادی)

یک نام دامنه بسازید که به IP سرور اشاره کند. پورت‌های 80 و 443 باید از اینترنت باز باشند. سپس در `.env`:

```env
SITE_ADDRESS=monitor.example.com
HTTP_BIND=127.0.0.1
```

`HTTP_BIND=127.0.0.1` باعث می‌شود پورت 8080 از بیرون در دسترس نباشد و فقط Caddy به برنامه دسترسی داشته باشد.

```bash
docker compose --profile https up -d --build
```

Caddy گواهی Let's Encrypt را خودکار دریافت و تمدید می‌کند. Caddy روی شبکه‌ی داخلی Docker یک IP ثابت دارد (`CADDY_IP`) و برنامه هدرهای `X-Forwarded-*` را فقط از همین IP قبول می‌کند. به همین دلیل Rate Limit و Audit Log، IP واقعی کاربر را ثبت می‌کنند.

### استفاده از nginx خودتان به جای Caddy

اگر روی خود سرور nginx دارید:

```env
HTTP_BIND=127.0.0.1
PROXY_IP=172.30.0.1
```

`172.30.0.1` آدرس Gateway شبکه‌ی Docker است، یعنی درخواست‌های nginx از این IP به کانتینر می‌رسند. نمونه‌ی تنظیم nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 1h;
}
```

هدرهای `Upgrade` و `Connection` الزامی هستند، چون Blazor Server و SignalR از WebSocket استفاده می‌کنند.

### سروری که به اینترنت دسترسی ندارد

اگر سرور به `mcr.microsoft.com`، Docker Hub یا NuGet دسترسی ندارد، Image را روی سیستمی که اینترنت دارد بسازید و منتقل کنید:

```bash
docker compose build app
```

```bash
docker pull postgres:17-alpine
```

```bash
docker save servermonitor:latest postgres:17-alpine | gzip > servermonitor-images.tar.gz
```

فایل را به سرور منتقل کنید و آنجا اجرا کنید:

```bash
gunzip -c servermonitor-images.tar.gz | docker load
```

```bash
docker compose up -d
```

دقت کنید که این بار `--build` نزنید. اگر از پروفایل https استفاده می‌کنید، `caddy:2.8-alpine` را هم به همین روش منتقل کنید.

> UI فایل‌های Bootstrap و ApexCharts را از CDN (jsDelivr) بارگذاری می‌کند. پس **مرورگر کاربر** باید به اینترنت دسترسی داشته باشد، نه خود سرور. برای شبکه‌ی کاملاً ایزوله، بخش [محدودیت‌ها](#محدودیتهای-شناختهشده) را ببینید.

### سخت‌سازی کانتینرها

- کانتینر برنامه با کاربر غیر root (`app`) اجرا می‌شود و فایل‌سیستمش فقط‌خواندنی است (`read_only`). فقط `/app/keys` و `/tmp` قابل نوشتن هستند.
- همه‌ی Linux Capabilityها حذف شده‌اند (`cap_drop: ALL`) و `no-new-privileges` فعال است.
- ICMP از طریق سوکت‌های Ping بدون نیاز به دسترسی ویژه کار می‌کند (`net.ipv4.ping_group_range`).
- PostgreSQL هیچ پورتی به بیرون باز نمی‌کند و فقط از شبکه‌ی داخلی در دسترس است.
- لاگ‌های Docker خودکار چرخانده می‌شوند: حداکثر 5 فایل 20 مگابایتی برای هر سرویس.

---

## اجرای محلی بدون Docker (توسعه)

پیش‌نیازها: .NET SDK 10 و PostgreSQL نسخه‌ی 14 یا بالاتر.

```bash
dotnet tool restore
```

```bash
dotnet test
```

متغیرهای محیطی را تنظیم کنید. نمونه‌ی زیر برای PowerShell است؛ در لینوکس از `export` استفاده کنید.

```powershell
$env:ConnectionStrings__Monitor = "Host=localhost;Port=5432;Database=servermonitor;Username=servermonitor;Password=..."
$env:Seed__AdminEmail = "admin@example.com"
$env:Seed__AdminPassword = "a-strong-password-12+"
$env:Https__Redirect = "false"
$env:DataProtection__KeysPath = ".dev-keys"
```

```bash
dotnet run --project src/ServerMonitor.Web --urls http://localhost:5080
```

Migrationها هنگام شروع برنامه خودکار اعمال می‌شوند. برای ساخت Migration جدید:

```bash
dotnet ef migrations add <Name> -p src/ServerMonitor.Infrastructure -s src/ServerMonitor.Infrastructure -o Persistence/Migrations
```

---

## آماده‌سازی سرورهای مقصد

### Linux (SSH)

یک کاربر اختصاصی و **بدون دسترسی root** بسازید. ترجیحاً از احراز هویت با کلید استفاده کنید.

```bash
sudo useradd -m -s /bin/bash monitor
```

```bash
sudo usermod -aG adm,systemd-journal monitor
```

- گروه‌های `adm` و `systemd-journal` اجازه‌ی خواندن لاگ‌های auth، syslog، nginx، apache و Journal کامل را می‌دهند. بدون آن‌ها بخش‌های امنیت و لاگ فقط چیزهایی را نشان می‌دهند که این کاربر اجازه‌ی خواندنشان را دارد.
- برای ورود با کلید، کلید عمومی را در `~monitor/.ssh/authorized_keys` قرار دهید و کلید خصوصی را در فرم سرور وارد کنید.
- اثر انگشت کلید میزبان را روی خود سرور بگیرید و با مقداری که **Test connection** نشان می‌دهد مقایسه کنید:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

**اختیاری: اجازه‌ی Restart سرویس‌ها.** فقط سرویس‌های مشخص را مجاز کنید. مثلاً در فایل `/etc/sudoers.d/servermonitor`:

```
monitor ALL=(root) NOPASSWD: /usr/bin/systemctl restart nginx.service, /usr/bin/systemctl restart php8.3-fpm.service
```

سپس در فرم سرور گزینه‌ی *Allow Operators to restart monitored services* را فعال کنید.

**دستوراتی که اجرا می‌شوند** همگی فقط‌خواندنی هستند: `cat /proc/*`، `df`، `hostname`، `who`، `getconf`، `ps -eo pid,user`، `systemctl show`، `journalctl`، `tail` روی مسیرهای ثابت لاگ، `lsblk`، و برای بررسی به‌روزرسانی‌ها `apt-check`، `apt list` یا `dnf --cacheonly`.

### Windows Server (WinRM روی HTTPS)

این دستورات را روی سرور مقصد با دسترسی Administrator اجرا کنید.

ساخت گواهی:

```powershell
$cert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -CertStoreLocation Cert:\LocalMachine\My
```

ساخت Listener روی HTTPS:

```powershell
New-Item -Path WSMan:\localhost\Listener -Transport HTTPS -Address * -CertificateThumbPrint $cert.Thumbprint -Force
```

باز کردن پورت 5986 در فایروال:

```powershell
New-NetFirewallRule -DisplayName "WinRM HTTPS" -Direction Inbound -Protocol TCP -LocalPort 5986 -Action Allow
```

- از یک حساب اختصاصی استفاده کنید که عضو گروه‌های **Remote Management Users**، **Performance Monitor Users** و **Event Log Readers** باشد. گروه آخر برای خواندن لاگ Security لازم است.
- دسترسی Administrator فقط برای خواندن وضعیت BitLocker و، در صورت فعال بودن، Restart سرویس‌ها لازم است.
- روش احراز هویت: *Negotiate/NTLM* (دامنه یا محلی) یا *Basic*. Basic فقط روی HTTPS مجاز است و باید آن را روی سرور فعال کنید:

```powershell
winrm set winrm/config/service/auth '@{Basic="true"}'
```

- **Test connection** اثر SHA-256 گواهی Listener را نشان می‌دهد. با **Trust** آن را Pin کنید.
- فقط اسکریپت‌های PowerShell که داخل برنامه کامپایل شده‌اند اجرا می‌شوند. هر اسکریپت فشرده و در قالب یک `-EncodedCommand` ارسال می‌شود.

---

## راهنمای صفحات برنامه

منوی کناری را می‌توان با دکمه‌ی بالای صفحه جمع کرد. نشانگر سبز **Live** در نوار بالا یعنی اتصال بلادرنگ برقرار است.

### Overview (`/`)

داشبورد اصلی با این بخش‌ها:
- **کارت‌های آماری:** تعداد کل سرورها، Online، Offline، Warning، Critical، هشدارهای باز، میانگین CPU، RAM و Disk، میانگین تأخیر ICMP و میانگین زمان پاسخ HTTP.
- **کارت هر سرور:** وضعیت سلامت، CPU، RAM، Disk، سرعت شبکه، Ping و Uptime.

بالای صفحه می‌توانید بر اساس گروه فیلتر کنید یا بر اساس نام، IP و تگ جستجو کنید. با کلیک روی هر کارت، جزئیات آن سرور باز می‌شود.

### Servers (`/servers`)

لیست همه‌ی سرورها با وضعیت، سیستم‌عامل، گروه، تگ‌ها و آخرین زمان دیده‌شدن.
- **Add server** و **Edit** (فقط Admin) فرم سرور را باز می‌کنند.
- **Delete** سرور و تمام داده‌های آن را حذف می‌کند (فقط Admin).
- دکمه‌ی **Groups** برای ساخت و مدیریت گروه‌ها است.

### فرم سرور (`/servers/new` و `/servers/{id}/edit`، فقط Admin)

| فیلد | توضیح |
|---|---|
| Name، Host | نام نمایشی و آدرس IP یا DNS |
| Operating system، Protocol | Linux با SSH؛ یا Windows با WinRM HTTPS (پیشنهادی) یا WinRM HTTP |
| Port | پورت مدیریت: 22 برای SSH، 5986 برای WinRM HTTPS، 5985 برای WinRM HTTP |
| Username، Auth method | رمز عبور یا کلید خصوصی (به همراه passphrase اختیاری). برای ویندوز: Negotiate یا Basic |
| Password / Private key | **فقط‌نوشتنی**. پس از ذخیره هرگز دوباره نمایش داده نمی‌شوند. اگر هنگام ویرایش خالی بمانند، مقدار قبلی حفظ می‌شود |
| Interval | فاصله‌ی پایش بر حسب ثانیه، بین 10 تا 3600. پیش‌فرض 30 |
| Group، Tags، Location، Description | برای دسته‌بندی، فیلتر و جستجو |
| Health check URL، Expected status | آدرس اختیاری برای پروب HTTP(S). زمان‌بندی فازها و انقضای گواهی TLS از همین آدرس خوانده می‌شود |
| TCP probe port | پورت مورد استفاده برای پروب TCP Connect. پیش‌فرض همان پورت مدیریت است |
| ICMP enabled | فعال یا غیرفعال بودن Ping |
| RDP port | فقط برای اطلاعات |
| Allow service control | اجازه‌ی Restart سرویس‌ها توسط Operatorها |
| Monitoring enabled | توقف یا ادامه‌ی پایش. زمانی که پایش متوقف است در SLA حساب نمی‌شود |
| Monitored services | سرویس‌هایی که باید پایش شوند. از لیست سرویس‌های شناخته‌شده انتخاب کنید یا نام دلخواه وارد کنید |
| Monitored ports | پورت‌های TCP که باید باز یا بسته بودنشان بررسی شود (حداکثر 64 پورت) |

دکمه‌ی **Test connection** اتصال را امتحان می‌کند و نتیجه، اثر انگشت کلید یا گواهی و نسخه‌ی سیستم‌عامل را نشان می‌دهد. تا زمانی که **Trust** نزنید، جمع‌آوری اطلاعات انجام نمی‌شود.

### جزئیات سرور (`/servers/{id}`)

بالای صفحه وضعیت، آخرین خطا و دکمه‌ی **Poll now** (پایش فوری) قرار دارد. صفحه این تب‌ها را دارد:

| تب | محتوا |
|---|---|
| **Overview** | خلاصه‌ی CPU، RAM، Swap و Disk؛ اطلاعات سیستم؛ Volumeها و Interfaceهای شبکه |
| **Performance** | نمودارهای CPU، Load، RAM، Swap، دیسک، Disk I/O، شبکه، TCP و پروسس‌ها. انتخاب بازه‌ی زمانی، Zoom، و خروجی **Metrics CSV** و **Probe CSV** |
| **Latency & response** | آمار جداگانه‌ی ICMP، TCP و HTTP (Min، Max، Avg، P50 تا P99، Jitter، Loss)، تفکیک فازهای HTTP و نمودارها |
| **Processes** | لیست زنده‌ی پروسس‌ها، قابل مرتب‌سازی بر اساس CPU و RAM، با دکمه‌ی Refresh |
| **Services** | وضعیت سرویس‌های پایش‌شده و دکمه‌ی Restart (در صورت مجاز بودن) |
| **Ports** | وضعیت پورت‌های تعریف‌شده و زمان پاسخ آن‌ها |
| **Security** | رخدادهای امنیتی، فایروال، پورت‌های Listen، به‌روزرسانی‌ها، رمزنگاری دیسک و گواهی TLS |
| **Availability / SLA** | درصد Uptime، Downtime، MTTR، MTBF، Error Budget و Timeline رنگی |

### Performance (`/performance`)

مقایسه‌ی چند سرور روی یک نمودار، برای CPU، RAM، Disk، شبکه و غیره، در بازه‌ی زمانی دلخواه.

### Network & latency (`/network`)

جدول مقایسه‌ی شبکه‌ی همه‌ی سرورها. برای هر سرور: میانگین، کمینه و بیشینه‌ی Ping؛ Jitter؛ Packet Loss؛ زمان TCP Connect؛ زمان کل HTTP و فازهای DNS، TLS و TTFB؛ کد وضعیت HTTP؛ سرعت دریافت و ارسال؛ و خطاهای شبکه. Percentileها و نمودارهای هر سرور در تب **Latency & response** صفحه‌ی جزئیات آن سرور هستند.

### Service monitoring (`/services`)

وضعیت همه‌ی سرویس‌های پایش‌شده در تمام سرورها، با فیلتر وضعیت. Operatorها می‌توانند از همین صفحه سرویس‌ها را Restart کنند (در صورت مجاز بودن).

### Port monitoring (`/ports`)

همه‌ی پورت‌های تعریف‌شده در همه‌ی سرورها، به همراه وضعیت باز یا بسته، زمان پاسخ و آخرین تغییر.

### Security monitoring (`/security`)

صفحه دو بخش دارد:
- **جدول خلاصه:** برای هر سرور تعداد ورودهای ناموفق و موفق، وضعیت فایروال، پورت‌های Listen، به‌روزرسانی‌های (امنیتی) معوق، نیاز به Reboot، رمزنگاری دیسک، گواهی TLS و زمان آخرین اسکن.
- **لیست رخدادها:** ورود ناموفق یا مشکوک، تغییر فایروال، پورت جدید، تغییر سرویس، به‌روزرسانی معوق و گواهی رو به انقضا، با فیلتر نوع رخداد و تعداد روز.

### Logs (`/logs`، Operator و بالاتر)

ابتدا سرور و منبع لاگ را انتخاب کنید:
- **لینوکس:** Journal، Syslog، Auth، Kernel، Nginx access و error، Apache access و error.
- **ویندوز:** System، Application، Security، Setup.

امکانات این صفحه: جستجو با هایلایت، فیلتر سطح (Error، Warning، Info و …)، فیلتر زمان، **Live tail** (به‌روزرسانی خودکار)، کلیک روی هر خط برای دیدن پیام کامل، و خروجی CSV. هر بار مشاهده و خروجی گرفتن از لاگ‌ها در Audit Log ثبت می‌شود.

### Alerts (`/alerts`)

مرکز هشدارها: لیست هشدارهای Open، Acknowledged و Resolved با فیلتر وضعیت، شدت و سرور.
- **Acknowledge:** یعنی «دیدم و دارم بررسی می‌کنم». ارسال دوباره‌ی اعلان برای این هشدار متوقف می‌شود.
- **Resolve:** بستن دستی هشدار. اگر شرط هشدار برطرف شود، هشدار خودکار هم Resolve می‌شود.

هر دو کار به نقش Operator یا بالاتر نیاز دارند. تعداد هشدارهای Critical باز در منوی کناری نمایش داده می‌شود.

### Alert rules (`/alerts/rules`، فقط Admin)

تعریف و ویرایش قوانین هشدار. توضیحات کامل در بخش [هشدارها و اعلان‌ها](#هشدارها-و-اعلانها) آمده است.

### Reports (`/reports`)

گزارش دسترس‌پذیری و SLA برای همه‌ی سرورها. بازه‌ها: Daily، Weekly، Monthly و Previous month. ستون‌ها: Uptime %، Downtime، تعداد Incident، MTTR، MTBF، Error Budget و مقایسه با هدف SLA. خروجی‌ها:
- **CSV**
- **PDF** از طریق Print مرورگر. برای چاپ، استایل روشن مخصوص اعمال می‌شود.

### Users (`/users`، فقط Admin)

- **New user:** ساخت کاربر با ایمیل، رمز (حداقل 12 کاراکتر) و نقش.
- **Edit:** تغییر نقش یا تعیین رمز جدید.
- **Sign out all sessions:** خروج اجباری کاربر از همه‌ی دستگاه‌ها. حداکثر ظرف یک دقیقه اعمال می‌شود.
- **Unlock:** باز کردن قفل حسابی که پس از 5 تلاش ناموفق برای 15 دقیقه قفل شده است.
- **Delete:** حذف کاربر.

### Audit log (`/audit`، فقط Admin)

ثبت همه‌ی کارهای حساس: ورود موفق و ناموفق، قفل شدن حساب، هر تغییر در سرورها، قوانین، کانال‌ها، تنظیمات و کاربران، تست اتصال، Trust کردن کلید، Restart سرویس، و مشاهده یا خروجی گرفتن از لاگ‌ها. برای هر مورد زمان، کاربر، IP، نوع عملیات و موجودیت هدف ثبت می‌شود. می‌توانید بر اساس عملیات، کاربر یا جزئیات جستجو کنید.

### Settings (`/settings`، فقط Admin)

**Collection (جمع‌آوری):**

| تنظیم | پیش‌فرض | توضیح |
|---|---|---|
| ICMP packets per round | 4 | تعداد Ping در هر دور پایش (1 تا 20) |
| Probe timeout (ms) | 3000 | مهلت هر پروب ICMP، TCP یا HTTP |
| Max concurrent collections | 16 | حداکثر تعداد سرورهایی که هم‌زمان پایش می‌شوند |
| Security scan interval (minutes) | 5 | فاصله‌ی اسکن امنیتی، که سنگین‌تر از پایش معمولی است |
| SLA target (%) | 99.9 | هدف SLA برای گزارش‌ها و محاسبه‌ی Error Budget |
| Certificate warning (days) | 21 | از چند روز قبل از انقضای گواهی، رخداد امنیتی ثبت شود |

**Data retention (نگهداری داده):** داده‌های قدیمی‌تر از این مقادیر هر یک ساعت یک‌بار خودکار پاک می‌شوند.

| داده | پیش‌فرض (روز) |
|---|---|
| Metrics | 14 |
| Probes | 14 |
| Resolved alerts | 180 |
| Security events | 90 |
| Audit log | 365 |

**Notification channels (کانال‌های اعلان):** در بخش بعد توضیح داده شده‌اند.

---

## هشدارها و اعلان‌ها

### هر قانون هشدار از چه بخش‌هایی تشکیل شده است

| فیلد | توضیح |
|---|---|
| Name | نام قانون |
| Server | خالی یعنی قانون **سراسری** و روی همه‌ی سرورها اعمال می‌شود. اگر سروری انتخاب شود، قانون فقط مخصوص همان سرور است |
| Metric | معیار مورد بررسی (جدول پایین) |
| Operator، Threshold | مقایسه با آستانه، مثلاً `> 85` |
| Severity | سطح هشدار: Info، Warning یا Critical |
| Consecutive breaches | شرط باید چند دور پشت‌سرهم برقرار باشد تا هشدار صادر شود. این کار از هشدارهای ناشی از نوسان لحظه‌ای جلوگیری می‌کند |
| Cooldown (minutes) | کمترین فاصله‌ی ارسال دوباره‌ی اعلان برای هشداری که هنوز باز است |
| Enabled | فعال یا غیرفعال بودن قانون |

**معیارهای قابل استفاده:**
- CPU %، Memory %، Disk %، Swap %، Load per core
- Ping latency (ms)، Packet loss %، HTTP response time (ms)
- Server offline، Service stopped، Port closed
- Certificate expiry (days)، Failed logins per interval

### قوانین پیش‌فرض

| قانون | شرط | سطح |
|---|---|---|
| CPU high | CPU > 85% به مدت 3 دور | Warning |
| CPU critical | CPU > 95% به مدت 3 دور | Critical |
| Memory high | RAM > 90% | Warning |
| Disk high | Disk > 90% | Critical |
| Ping latency | Ping > 150ms | Warning |
| Packet loss | Loss > 5% | Warning |
| HTTP slow | HTTP > 500ms | Warning |
| Server offline | Offline به مدت 2 دور | Critical |
| Service stopped | سرویس پایش‌شده متوقف شود | Critical |
| Certificate expiring | کمتر از 14 روز تا انقضا (Cooldown 12 ساعت) | Warning |
| Port closed | پورت پایش‌شده به مدت 2 دور بسته باشد | Warning |
| Brute force | بیش از 20 ورود ناموفق (Cooldown 1 ساعت) | Warning |

### رفتار موتور هشدار

- **Deduplication:** برای هر ترکیب «قانون + سرور + موضوع» فقط یک هشدار باز وجود دارد. مثلاً برای هر دیسک یا هر سرویس یک هشدار جداگانه ساخته می‌شود، نه یک هشدار تکراری در هر دور.
- **Cooldown:** تا وقتی هشدار باز و Acknowledge نشده است، اعلان آن حداکثر یک بار در هر بازه‌ی Cooldown دوباره ارسال می‌شود.
- **Auto-resolve:** وقتی شرط برطرف شود، هشدار خودکار Resolve می‌شود. اگر روی کانال گزینه‌ی *Notify on resolve* فعال باشد، اعلان رفع مشکل هم ارسال می‌شود.

### کانال‌های اعلان (Settings → Notification channels)

برای هر کانال این گزینه‌ها وجود دارد: نام، **حداقل سطح** (مثلاً فقط Critical)، فعال یا غیرفعال، و Notify on resolve. دکمه‌ی **Send test** یک پیام آزمایشی می‌فرستد.

| نوع | تنظیمات |
|---|---|
| **Email** | SMTP host، Port (پیش‌فرض 587)، TLS، Username، Password، From، To (چند آدرس با کاما جدا می‌شوند) |
| **Telegram** | Bot token (از BotFather) و Chat ID. ربات باید عضو گروه یا کانال مقصد باشد |
| **Webhook** | URL و HMAC secret اختیاری. بدنه به صورت JSON و با متد POST ارسال می‌شود. اگر Secret تعیین شده باشد، هدر `X-ServerMonitor-Signature: sha256=<HEX>` اضافه می‌شود که برابر HMAC-SHA256 بدنه با کلید Secret است |

رمز SMTP، توکن ربات و Secret وب‌هوک رمزنگاری‌شده ذخیره می‌شوند و هرگز دوباره در UI نمایش داده نمی‌شوند. اعلان‌ها در یک صف جداگانه ارسال می‌شوند، پس کندی سرویس ایمیل یا تلگرام روی پایش اثری ندارد.

---

## نقش‌ها و دسترسی‌ها

| کار | Viewer | Operator | Admin |
|---|:---:|:---:|:---:|
| دیدن داشبورد، سرورها، نمودارها، تأخیر، سرویس‌ها، پورت‌ها، امنیت، هشدارها و گزارش‌ها | ✅ | ✅ | ✅ |
| خروجی CSV و PDF | ✅ | ✅ | ✅ |
| مشاهده‌ی لاگ‌ها | ❌ | ✅ | ✅ |
| Acknowledge و Resolve هشدارها | ❌ | ✅ | ✅ |
| Restart سرویس (اگر روی سرور مجاز باشد) | ❌ | ✅ | ✅ |
| افزودن، ویرایش و حذف سرور؛ تست اتصال و Trust | ❌ | ❌ | ✅ |
| قوانین هشدار، کانال‌های اعلان، تنظیمات | ❌ | ❌ | ✅ |
| مدیریت کاربران و Audit Log | ❌ | ❌ | ✅ |

دسترسی‌ها هم در صفحات UI و هم در سرویس‌های Backend بررسی می‌شوند. بنابراین با فراخوانی مستقیم API هم نمی‌توان آن‌ها را دور زد.

---

## معماری

پروژه بر اساس **Clean Architecture** ساخته شده است:

```
src/
 ├─ ServerMonitor.Domain           موجودیت‌ها و Enumها (بدون وابستگی)
 ├─ ServerMonitor.Application      قراردادها (IServerAdapter، INetworkProber، IRealtimePublisher، …)،
 │                                 مدل‌های زنده، محاسبات خالص (Percentile، Jitter، SLA، MTTR، MTBF)
 ├─ ServerMonitor.Infrastructure
 │   ├─ Persistence                EF Core DbContext (PostgreSQL)، Migrationها، Identity
 │   ├─ Security                   رمزنگاری Credentialها، اعتبارسنجی ورودی‌ها
 │   ├─ Remote/Ssh                 Pool اتصالات SSH، Pin کردن کلید میزبان
 │   ├─ Remote/WinRm               کلاینت WS-Management، Pin کردن گواهی
 │   ├─ Adapters/Linux             اسکریپت‌های ثابت + Parserهای خالص (/proc، systemd، journald)
 │   ├─ Adapters/Windows           اسکریپت‌های ثابت PowerShell/CIM + Parserها (Raw perf counters)
 │   ├─ Probes                     پروب ICMP، TCP و HTTP(S) با زمان‌بندی فازها
 │   ├─ Monitoring                 Scheduler (BackgroundService)، Collector، Cache زنده،
 │   │                             Retry/Backoff، Retention Worker
 │   ├─ Alerting                   موتور هشدار، صف اعلان، Email، Telegram، Webhook
 │   └─ Services                   سرویس‌های کاربردی سرور، هشدار و متریک (مشترک بین UI و API)
 └─ ServerMonitor.Web              Blazor UI، SignalR Hub، REST API، احراز هویت، Rate Limiting
tests/ServerMonitor.Tests          تست‌های xUnit برای Parserها، آمار، SLA، اعتبارسنجی، Retry
```

### جریان داده

```
MonitoringScheduler (هر 1 ثانیه یک Tick)
  └─ برای هر سرور در زمان خودش؛ حداکثر N سرور موازی؛ بدون هم‌پوشانی؛ Backoff نمایی برای سرور در دسترس نیست
       └─ ServerCollector
            ├─ NetworkProber: چند ICMP، یک TCP Connect، فازهای HTTP(S)   (از روی سرور مانیتورینگ)
            ├─ IServerAdapter (Linux با SSH / Windows با WinRM): متریک‌ها و سرویس‌ها
            ├─ بررسی پورت‌ها
            ├─ ذخیره: MetricSnapshots، ProbeResults، StatusPeriods، SecurityEvents
            ├─ AlertEngine → Alerts → NotificationQueue → NotificationWorker → Email/Telegram/Webhook
            └─ ILiveStateCache → IRealtimePublisher → SignalR Hub + Blazor
```

- **Scheduler با نرخ ثابت:** فاصله‌ی هر پایش از *شروع* دور قبلی حساب می‌شود، پس زمان‌بندی نمونه‌ها جابه‌جا نمی‌شود. اگر سروری در دسترس نباشد، فاصله‌ی تلاش‌ها به صورت نمایی زیاد می‌شود، تا حداکثر 4 برابر Interval یا 5 دقیقه. اولین پایش سرورها هم کمی پخش می‌شود تا همه هم‌زمان وصل نشوند.
- **نرخ‌ها واقعی هستند:** CPU، Disk I/O، شبکه و CPU هر پروسس دو بار با **یک ثانیه فاصله روی خود سرور** نمونه‌برداری می‌شوند. به این ترتیب هر پایش مقدار لحظه‌ای درست را برمی‌گرداند. CPU پروسس‌های لینوکس از `/proc/[pid]/stat` خوانده می‌شود، چون `ps` فقط میانگین کل عمر پروسس را می‌دهد.
- **پروب‌ها مستقل هستند:** زمان Ping، زمان TCP Handshake و زمان پاسخ HTTP سه اندازه‌گیری جدا هستند. هر نمونه (حتی هر Ping) یک ردیف جداگانه ذخیره می‌شود و Percentile، Jitter و Loss از همین داده‌های خام محاسبه می‌شوند.
- **وضعیت سرور:** سرور فقط وقتی *Offline* است که هم کانال مدیریت (SSH یا WinRM) و هم همه‌ی پروب‌ها شکست بخورند. اگر سرور در دسترس باشد ولی احراز هویت یا کلید میزبان مشکل داشته باشد، وضعیت *Warning* نمایش داده می‌شود. بازه‌های Up و Down مبنای Uptime، MTTR و MTBF هستند. زمانی که پایش غیرفعال بوده «Not monitored» حساب می‌شود و در SLA اثری ندارد.
- **چرا DbContext به صورت Transient ثبت شده:** اتصال Blazor Server طولانی‌مدت است. برای اینکه یک DbContext بین درخواست‌های هم‌زمان مشترک نشود، هر عملیات DbContext جداگانه می‌گیرد.

---

## مدل امنیتی

| موضوع | پیاده‌سازی |
|---|---|
| Credentialها روی دیسک | با ASP.NET Core Data Protection (AES-256-CBC + HMAC-SHA256) رمزنگاری می‌شوند. Key Ring در **Volume جداگانه** (`/app/keys`) نگهداری می‌شود و می‌توان آن را با یک گواهی X.509 هم محافظت کرد. **Dump پایگاه داده به تنهایی هیچ رمزی را لو نمی‌دهد** |
| Credentialها در UI، API و لاگ | فیلدها فقط‌نوشتنی هستند و هرگز به مرورگر یا API ارسال نمی‌شوند یا در لاگ نوشته نمی‌شوند |
| جعل میزبان SSH | تا زمانی که Admin کلید میزبان را Trust نکند، پایش انجام نمی‌شود. اگر کلید عوض شود، پایش متوقف و وضعیت `HostKeyMismatch` ثبت می‌شود |
| TLS در WinRM | Pin کردن SHA-256 گواهی، چون Listenerهای Self-signed رایج هستند. HTTP فقط با انتخاب صریح مجاز است |
| Command Injection | هیچ دستوری از Frontend نمی‌آید و اسکریپت‌ها ثابت و کامپایل‌شده‌اند. تنها مقادیری که در اسکریپت جایگذاری می‌شوند اعداد یا نام‌هایی هستند که با Regex لیست سفید بررسی و Quote شده‌اند. منابع لاگ هم کلیدهای ثابتی هستند که به دستورات ثابت نگاشت می‌شوند |
| حداقل دسترسی | حساب فقط‌خواندنی کافی است. Restart سرویس هم باید برای هر سرور جداگانه فعال شود و هم یک قانون محدود sudoers لازم دارد |
| احراز هویت و مجوز | ASP.NET Core Identity؛ رمز حداقل 12 کاراکتر؛ قفل حساب پس از 5 تلاش ناموفق به مدت 15 دقیقه؛ Cookie با انقضای لغزان 8 ساعته (`HttpOnly`، `SameSite=Strict`)؛ بررسی Security Stamp هر دقیقه (برای تغییر نقش یا خروج از همه‌جا) |
| CSRF و XSS | Antiforgery روی فرم‌ها؛ API برای درخواست‌های تغییردهنده هدر `X-CSRF` می‌خواهد؛ CORS فعال نیست؛ Razor همه‌ی خروجی‌ها را Encode می‌کند؛ CSP سخت‌گیرانه، `X-Frame-Options: DENY` و `nosniff` |
| Rate Limiting | ورود: 10 بار در دقیقه برای هر IP. API: Token Bucket برای هر کاربر (120 توکن، 60 توکن در هر 30 ثانیه) |
| انتقال داده | Redirect به HTTPS + HSTS، یا TLS روی Caddy |
| Audit | ورود و خروج، تغییرات پیکربندی، تست اتصال، Trust، Restart سرویس، مشاهده‌ی لاگ و مدیریت کاربران |
| اسکن | فقط پورت‌هایی که صریحاً تعریف شده‌اند بررسی می‌شوند (حداکثر 64 پورت برای هر سرور). اسکن بازه‌ای وجود ندارد |

---

## REST API و SignalR

همه‌ی Endpointها نشست واردشده (Cookie) لازم دارند. درخواست‌های تغییردهنده باید هدر `X-CSRF: 1` داشته باشند. پارامترهای `from` و `to` در قالب ISO-8601 هستند.

| متد | مسیر | توضیح |
|---|---|---|
| GET | `/api/v1/servers` | لیست سرورها (بدون Credential) به همراه وضعیت زنده |
| GET | `/api/v1/servers/{id}/live` | آخرین وضعیت زنده‌ی یک سرور |
| GET | `/api/v1/servers/{id}/metrics?from&to` | سری‌های زمانی Bucket‌شده به همراه Min، Max و Avg |
| GET | `/api/v1/servers/{id}/latency?type=Icmp\|Tcp\|Http&from&to` | آمار تأخیر (P50 تا P99، Jitter، Loss) به همراه سری زمانی |
| GET | `/api/v1/servers/{id}/http?from&to` | آمار فازهای HTTP |
| GET | `/api/v1/servers/{id}/availability?from&to` | Uptime، MTTR، MTBF و بازه‌ها |
| GET | `/api/v1/servers/{id}/export/metrics.csv` | خروجی خام متریک‌ها |
| GET | `/api/v1/servers/{id}/export/probes.csv` | خروجی خام پروب‌ها |
| GET | `/api/v1/alerts?status&severity&serverId` | لیست هشدارها |
| POST | `/api/v1/alerts/{id}/acknowledge` | Acknowledge کردن هشدار (Operator و بالاتر) |
| POST | `/api/v1/alerts/{id}/resolve` | Resolve کردن هشدار (Operator و بالاتر) |
| GET | `/api/v1/reports/sla?from&to` | گزارش SLA به صورت JSON |
| GET | `/api/v1/reports/sla.csv?from&to` | گزارش SLA به صورت CSV |
| GET | `/healthz` | بررسی سلامت برنامه (بدون نیاز به ورود) |

**SignalR:** Hub در مسیر `/hubs/monitoring` قرار دارد.
- رویدادها: `serverUpdated` (متریک‌ها و خلاصه‌ی پروب‌ها) و `alertChanged`.
- با `SubscribeServer(id)` عضو گروه یک سرور مشخص می‌شوید.

---

## مرجع تنظیمات

### متغیرهای `.env` (Docker Compose)

| متغیر | پیش‌فرض | توضیح |
|---|---|---|
| `DB_PASSWORD` | — (**الزامی**) | رمز کاربر PostgreSQL |
| `ADMIN_EMAIL` | `admin@local` | ایمیل مدیر اولیه |
| `ADMIN_PASSWORD` | خالی | رمز مدیر اولیه. اگر خالی باشد، رمز تصادفی در `initial-admin-password.txt` نوشته می‌شود |
| `TZ` | `UTC` | منطقه‌ی زمانی کانتینرها |
| `HTTP_BIND` | `0.0.0.0` | IP که پورت برنامه روی میزبان به آن Bind می‌شود. پشت Proxy مقدار `127.0.0.1` بگذارید |
| `HTTP_PORT` | `8080` | پورت برنامه روی میزبان |
| `SITE_ADDRESS` | `localhost` | دامنه برای Caddy (پروفایل `https`) |
| `DOCKER_SUBNET` | `172.30.0.0/24` | Subnet شبکه‌ی داخلی Docker. اگر با شبکه‌ی شما تداخل دارد، تغییرش دهید |
| `CADDY_IP` | `172.30.0.10` | IP ثابت Caddy در شبکه‌ی داخلی. باید داخل `DOCKER_SUBNET` باشد |
| `PROXY_IP` | خالی | IP یک Reverse Proxy خارجی که هدرهای `X-Forwarded-*` آن پذیرفته می‌شود |
| `APP_IMAGE` | `servermonitor:latest` | نام Image. برای Registry خصوصی تغییرش دهید |
| `DP_CERT_PATH`، `DP_CERT_PASSWORD` | خالی | فایل PFX اختیاری برای رمزنگاری Key Ring. فایل را Read-only در کانتینر Mount کنید |
| `DEMO_SSH_PASSWORD` | خالی | رمز کاربر `monitor` در پروفایل `demo` |

### کلیدهای پیکربندی برنامه

این کلیدها را می‌توان از `appsettings.json` یا با متغیر محیطی تنظیم کرد. در متغیر محیطی به جای `:` از `__` استفاده کنید.

| کلید | پیش‌فرض | توضیح |
|---|---|---|
| `ConnectionStrings:Monitor` | — | Connection String پایگاه داده‌ی PostgreSQL |
| `DataProtection:KeysPath` | `keys` | پوشه‌ی Key Ring. **از آن پشتیبان بگیرید**؛ اگر از دست برود، Credentialهای ذخیره‌شده قابل بازیابی نیستند |
| `DataProtection:CertificatePath` / `CertificatePassword` | — | فایل PFX برای رمزنگاری Key Ring |
| `Seed:AdminEmail` / `Seed:AdminPassword` | `admin@local` / تصادفی | فقط وقتی استفاده می‌شوند که هیچ کاربری وجود نداشته باشد |
| `Https:Redirect` | `true` | پشت Proxy که TLS را خاتمه می‌دهد، مقدار `false` بگذارید (در Docker پیش‌فرض `false` است) |
| `ReverseProxy:KnownProxies` | `[]` | لیست IPهایی که هدرهای `X-Forwarded-*` آن‌ها پذیرفته می‌شود |

تنظیمات زمان اجرا (Retention، هدف SLA، تعداد Ping، Timeout، هم‌زمانی و فاصله‌ی اسکن امنیتی) از صفحه‌ی **Settings** تغییر می‌کنند و نیازی به Restart ندارند.

---

## پشتیبان‌گیری، بازیابی و به‌روزرسانی

دو Volume حیاتی وجود دارد:

| Volume | محتوا |
|---|---|
| `servermonitor_pgdata` | پایگاه داده |
| `servermonitor_keys` | Key Ring. **بدون این Volume، رمزهای سرورها و کانال‌ها قابل رمزگشایی نیستند** |

این دو را جدا از هم نگهداری کنید. Key Ring را کنار Dump پایگاه داده قرار ندهید.

### پشتیبان‌گیری

پایگاه داده:

```bash
docker compose exec -T db pg_dump -U servermonitor servermonitor | gzip > db-$(date +%F).sql.gz
```

Key Ring:

```bash
docker run --rm -v servermonitor_keys:/k:ro -v "$PWD":/b alpine tar czf /b/keys-$(date +%F).tgz -C /k .
```

### بازیابی

برنامه را متوقف کنید:

```bash
docker compose stop app
```

پایگاه داده را بازیابی کنید:

```bash
gunzip -c db-YYYY-MM-DD.sql.gz | docker compose exec -T db psql -U servermonitor servermonitor
```

Key Ring را بازیابی کنید:

```bash
docker run --rm -v servermonitor_keys:/k -v "$PWD":/b alpine sh -c "tar xzf /b/keys-YYYY-MM-DD.tgz -C /k && chown -R 1654:1654 /k"
```

برنامه را دوباره اجرا کنید:

```bash
docker compose start app
```

> پیش از بازیابی Dump، پایگاه داده باید خالی باشد. اگر داده دارد، Volume `servermonitor_pgdata` را حذف کنید و `db` را دوباره بالا بیاورید. شناسه‌ی `1654` همان کاربر `app` در Image رسمی .NET است.

### به‌روزرسانی برنامه

```bash
git pull
```

```bash
docker compose up -d --build
```

Migrationهای پایگاه داده هنگام شروع برنامه خودکار اعمال می‌شوند. توصیه می‌شود قبل از هر به‌روزرسانی پشتیبان بگیرید.

---

## عیب‌یابی

### وضعیت‌های اتصال سرور

| وضعیت | معنی | راه‌حل |
|---|---|---|
| `Connected` | همه‌چیز درست است | — |
| `HostKeyNotTrusted` | کلید میزبان هنوز Trust نشده است | در فرم سرور **Test connection** و سپس **Trust** را بزنید |
| `HostKeyMismatch` | کلید میزبان با مقدار Trust‌شده فرق دارد | ممکن است سرور دوباره نصب شده باشد، یا **حمله‌ی MITM** در کار باشد. بررسی کنید و بعد دوباره Trust کنید |
| `AuthenticationFailed` | نام کاربری، رمز یا کلید اشتباه است | Credential را در فرم سرور اصلاح کنید |
| `Unreachable` | سرور یا پورت در دسترس نیست | فایروال، IP، پورت و Routing را بررسی کنید |
| `Error` | خطای دیگر | پیام کامل در بالای صفحه‌ی جزئیات سرور و در `docker compose logs app` |
| `Disabled` | پایش خاموش است | گزینه‌ی Monitoring enabled را روشن کنید |

### مشکلات رایج

- **برنامه بالا نمی‌آید یا `unhealthy` است:** خروجی `docker compose logs app` را ببینید. معمولاً `DB_PASSWORD` خالی است، یا Volume پایگاه داده قبلاً با رمز دیگری ساخته شده است. رمز PostgreSQL فقط در اولین اجرا تعیین می‌شود.
- **پس از ورود صفحه ثابت می‌ماند یا Live وصل نمی‌شود:** Proxy باید WebSocket را پشتیبانی کند (هدرهای `Upgrade` و `Connection`).
- **Ping همیشه 100% Loss است:** ممکن است ICMP در فایروال مقصد بسته باشد. در این حالت `ICMP enabled` را خاموش کنید؛ وضعیت سرور از TCP و HTTP تعیین می‌شود. اگر Ping در کانتینر کار نمی‌کند، نسخه‌ی Docker را به‌روز کنید (برای پشتیبانی از `sysctls`).
- **لاگ‌ها یا رخدادهای امنیتی لینوکس خالی هستند:** کاربر مانیتورینگ را به گروه‌های `adm` و `systemd-journal` اضافه کنید.
- **پس از انتقال به سرور جدید، رمزها کار نمی‌کنند:** Volume کلیدها منتقل نشده است. آن را از پشتیبان بازیابی کنید، یا Credential هر سرور را دوباره وارد کنید.
- **رمز Admin را فراموش کرده‌اید:** اگر Admin دیگری دارید، از صفحه‌ی Users رمز جدید تعیین کنید. در غیر این صورت، کاربر را مستقیماً از جدول `AspNetUsers` حذف کنید و برنامه را با `ADMIN_PASSWORD` جدید Restart کنید. Admin فقط وقتی دوباره ساخته می‌شود که هیچ کاربری وجود نداشته باشد.
- **تداخل Subnet:** اگر `172.30.0.0/24` با شبکه‌ای که مانیتور می‌کنید هم‌پوشانی دارد، `DOCKER_SUBNET` و `CADDY_IP` را عوض کنید.

---

## محدودیت‌های شناخته‌شده

- دقت RTT در ICMP یک میلی‌ثانیه است (`System.Net.NetworkInformation.Ping`)، پس Ping زیر یک میلی‌ثانیه در شبکه‌ی LAN صفر نمایش داده می‌شود. زمان‌های TCP و HTTP با Stopwatch دقیق اندازه‌گیری می‌شوند.
- دمای CPU در لینوکس از `/sys/class/thermal` و در ویندوز از `MSAcpi_ThermalZoneTemperature` خوانده می‌شود. بسیاری از سرورها و ماشین‌های مجازی این مقدار را ارائه نمی‌دهند و در این حالت «n/a» نمایش داده می‌شود.
- سلامت SMART دیسک به دسترسی root یا Admin نیاز دارد و جمع‌آوری نمی‌شود. تأخیر دیسک و وضعیت رمزنگاری جمع‌آوری می‌شوند.
- بررسی «به‌روزرسانی‌های معوق» در ویندوز از Windows Update Agent استفاده می‌کند که ممکن است ده‌ها ثانیه طول بکشد. به همین دلیل فقط در اسکن امنیتی کم‌تکرار اجرا می‌شود.
- Bootstrap، Bootstrap Icons و ApexCharts از نسخه‌های ثابت jsDelivr بارگذاری می‌شوند. برای شبکه‌ی کاملاً ایزوله، فایل‌ها را در `wwwroot/lib` کپی کنید و ارجاع‌ها را در `App.razor` و CSP را در `Program.cs` تغییر دهید.
- جمع‌آوری اطلاعات لینوکس روی سرورهای واقعی Ubuntu آزمایش شده است. کلاینت WinRM با Unit Test بررسی شده، ولی هنوز روی یک Windows Server واقعی امتحان نشده است. اولین سرور ویندوزی را با دقت بررسی کنید.

---

## ساختار پروژه

```
ServerMonitor/
├─ src/                     کد برنامه (بخش معماری را ببینید)
├─ tests/                   تست‌های xUnit
├─ deploy/Caddyfile         پیکربندی Caddy برای پروفایل https
├─ Dockerfile               Build چندمرحله‌ای: SDK 10 → aspnet:10 (non-root)
├─ docker-compose.yml       سرویس‌های db، app، proxy (https) و demo-target (demo)
├─ .env.example             نمونه‌ی تنظیمات استقرار
├─ Directory.Build.props    تنظیمات مشترک پروژه‌ها (net10.0، nullable)
├─ ServerMonitor.sln
└─ README.md
```

اجرای تست‌ها:

```bash
dotnet test
```

تست‌ها شامل Parserهای خروجی واقعی `/proc`، systemd، journald و CIM، محاسبات آماری، محاسبات SLA، اعتبارسنجی ورودی‌ها و Retry هستند.
