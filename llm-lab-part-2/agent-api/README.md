# LlmLab Agent API (Microsoft Agent Framework, katmanlı mimari)

`C:\dev\llm-lab\LlmApi` (tek proje, elle yazılmış RAG) ile **aynı davranışı ve aynı HTTP sözleşmesini** sunan, ama
**Microsoft Agent Framework** ve **Clean Architecture** ile yeniden kurulmuş sürüm. Amaç iki yaklaşımı yan yana
çalıştırıp kıyaslamak. Eski proje bilerek olduğu gibi bırakıldı.

| | Eski (`LlmApi`) | Yeni (`agent-api`) |
|---|---|---|
| Port / veritabanı | 5080 / `DevDb` | **5090** / `LlmLabAgent` |
| Arayüz | `npm run dev` (5173) | aynı `web/` projesi, ikinci örnek (5174), bkz. aşağısı |

## Katmanlar ve bağımlılık kuralı

```
        ┌────────────────────────────────────────────────────────────┐
        │  LlmLab.Api            HTTP, SSE, hata kapısı, bileşim kökü │  ince: iş mantığı yok
        └──────────────┬─────────────────────────────┬───────────────┘
                       │                             │
        ┌──────────────▼──────────────┐   ┌──────────▼────────────────┐
        │  LlmLab.Infrastructure      │   │                           │
        │  EF Core + SQL Server,      ├──►│  LlmLab.Application       │  kullanım senaryoları (handler'lar),
        │  Ollama, Agent Framework,   │   │  portlar (arayüzler),     │  saf iş mantığı, seçenekler
        │  PdfPig                     │   │  parçalama, istem metinleri│
        └─────────────────────────────┘   └──────────┬────────────────┘
                                                     │
                                          ┌──────────▼────────────────┐
                                          │  LlmLab.Domain            │  varlıklar, değişmezler; bağımlılık yok
                                          └───────────────────────────┘
```

**Kural: oklar yalnızca içe doğru.** Domain hiçbir şeyi, Application yalnızca Domain'i bilir; EF Core, Ollama ve Agent
Framework yalnızca Infrastructure'da yaşar. Application, altyapıyı **portlar** (arayüzler) üzerinden kullanır; gerçek
uygulamalar `Api/Program.cs` içinde (bileşim kökü) bağlanır. Sonuç: çatıyı değiştirmek (Agent Framework → LangChain/
düz `IChatClient`), veritabanını değiştirmek ya da testte sahte bağımlılık vermek tek bir katmanı etkiler.

```
src/
  LlmLab.Domain/           Conversation, ConversationMessage, SourceReference, KnowledgeDocument, DocumentChunk, EmbeddingSpec
  LlmLab.Application/
    Abstractions/          IChatAgent, IEmbeddingService, IChunkSearcher, IDocumentSearch, ITextExtractor, repository'ler, IUnitOfWork
    Features/              Conversations/, Documents/ (yükleme, arama), Chat/ (StreamChatHandler)
    Rag/                   TextChunker, QueryTokenizer, AgentPrompts   <- saf fonksiyonlar, birim testli
    Options/               RagOptions, AgentSettings                   <- başlangıçta doğrulanır
  LlmLab.Infrastructure/
    Persistence/           AppDbContext, yapılandırmalar, depolar, ChunkSearcher (VECTOR_DISTANCE), Migrations/
    Ai/                    MafChatAgent (Agent Framework), OllamaEmbeddingService, OllamaOptions, OllamaHealthCheck
    Files/                 TextExtractor (txt/md/pdf)
  LlmLab.Api/              Endpoints/, Streaming/SseWriter, Middleware/GlobalExceptionHandler, Program.cs
tests/
  LlmLab.Application.Tests/   44 test: parçalayıcı, sorgu ayrıştırıcı, istem metinleri, orkestratör, yükleme, etki alanı
```

## Temel tasarım farkları (eski ↔ yeni)

| Konu | Eski | Yeni |
|---|---|---|
| **Sohbet geçmişi** | Her istekte son 20 mesaj DB'den okunup elle `ChatMessage` listesi kurulur | **`AgentSession`** ajanın çalışma belleğidir; tur sonunda serileştirilip `Conversation.AgentSessionState`'e yazılır, sonraki turda geri yüklenir. Geçmişi kırpmak `MessageCountingChatReducer`'ın işi |
| **Belge araması** | "Belgeler" açıksa **her** mesajda zorla yapılır | **Ayrı bir MCP sunucusunda** (`LlmLab.Mcp.Documents`) `search_documents` aracı; ajan onu MCP istemcisi olarak çağırır ve çağırıp çağırmamaya model karar verir (agentic RAG). Eski sürümdeki `SearchFirst` ön araması **kaldırıldı** (bkz. "Araçlar ve MCP") |
| **Kaynakları modele verme** | Kullanıcı mesajının içine etiketli veri olarak eklenir | Aracın dönüş değeri olarak, aynı etiketli biçimle. Birden çok çağrıda `[n]` numaraları tutarlı |
| **Transkript ↔ bellek** | Tek tablo ikisini de temsil eder | **Ayrı:** `Messages` = kullanıcının gördüğü transkript (düşünce, süre, kaynaklar); `AgentSessionState` = ajanın belleği. Okuma modeli / çalışma belleği ayrımı |
| **Orkestrasyon** | `Program.cs` içinde ~100 satırlık uç nokta | `StreamChatHandler` (Application): olay akışını tüketir, kalıcılığı yönetir; çatıyı bilmez |
| **Ajan çatısı** | `IChatClient` doğrudan | `IChatAgent` portu arkasında Agent Framework `ChatClientAgent` |
| **Hatalar** | Elle `BadHttpRequestException` | `AppException` hiyerarşisi + tek `GlobalExceptionHandler` (ProblemDetails) |
| **Ayarlar** | `IConfiguration` ile dağınık | Tipli `Options` + `ValidateOnStart`: yanlış ayar uygulama açılırken patlar |
| **Sağlık / belgeleme** | yok | `/health` (DB + Ollama), `/openapi/v1.json` (geliştirmede) |
| **Gözlemlenebilirlik** | yalnızca konsol günlüğü | OpenTelemetry: iz + metrik + günlük, `gen_ai.*` model/token ölçümü, özel `llmlab.*` metrikleri (aşağıya bakın) |
| **Test** | yok | 50 birim testi, sahte bağımlılıklarla (Ollama/DB gerekmez); telemetri de test edilir |
| **Derleme kuralları** | varsayılan | `TreatWarningsAsErrors` + NuGet audit: **bilinen açığı olan paket derlemeyi durdurur** |
| **Boyut** | 7 dosya, 594 satır | 37 dosya, 1.229 satır (+ 456 satır test) |

> **Kıyaslama notu:** Katmanlı yapı kod miktarını yaklaşık 2 katına çıkarır; karşılığında test edilebilirlik, çatı
> değiştirilebilirliği ve sorumlulukların net ayrımı gelir. 600 satırlık bir prototip için bu fazla, birkaç ekip /
> yıllarca yaşayacak bir ürün için doğru yatırımdır. Hangisinin "daha iyi" olduğu bağlama bağlıdır.

## Çalıştırma

Önkoşullar: Ollama çalışıyor; `qwen3:8b`, `qwen3-embedding:0.6b` inmiş; CPU embedding varyantı oluşturulmuş
(`ollama create qwen3-embed-cpu -f ollama/Modelfile.qwen3-embed-cpu`, bkz. ana README "GPU belleği"); yerel SQL Server.

Beş süreç gerekir: API (5090), üç MCP sunucusu (araçlar 5101, belgeler 5102, sandbox 5103) ve A2A servisi (5201).
Betik derler ve hepsini doğru sırayla başlatır (A2A servisi, API ajanları oluşturduktan sonra):

```powershell
cd C:\dev\llm-lab\llm-lab-part-2\agent-api
.\scripts\dev.ps1            # üç ayrı pencere; -Hidden: pencere yok, günlükler %TEMP%\llmlab-*.log; -Stop: durdur
```

Geliştirmede veritabanını (LlmLabAgent) API'nin migration'ı oluşturur ve başlangıç ajanlarını ekler; belge MCP sunucusu ve
A2A servisi aynı veritabanını kullanır, şemayı yönetmez. Bir MCP sunucusu kapalıysa ajan o araçlar olmadan çalışmaya
devam eder (talimat da buna göre kurulur). Sandbox için bir kez: `docker pull python:3.12-slim`.

İkinci arayüz örneği (aynı `web/` kodu, farklı API'ye bakar; CORS'ta 5174 izinlidir):

```powershell
cd C:\dev\llm-lab\llm-lab-part-2\web
$env:VITE_API_URL = 'http://localhost:5090'; npm.cmd run dev -- --port 5174 --strictPort
```

Böylece `http://localhost:5173` (eski) ve `http://localhost:5174` (yeni) yan yana açılıp aynı soru iki sürüme sorulabilir.
Yeni sürüm ek olarak `tool` olayları yayar; arayüz bunları cevabın **metinleri arasında**, tıklanınca açılan bir satır
olarak gösterir ("🔍 Belgelerde arandı"). Araç çağrıları, cevap metnindeki **konumuyla** birlikte transkriptte saklanır
(`Messages.ToolCallsJson`); sohbet yeniden açılınca da aynı yerde görünür. Eski API bu olayı göndermez, onun arayüzü değişmez.

Testler: `dotnet test LlmLab.slnx` (Application + MCP testleri; MCP testleri sunucuyu süreç içinde çalıştırır)
Migration eklemek: `dotnet ef migrations add <Ad> -p src/LlmLab.Infrastructure -s src/LlmLab.Api -o Persistence/Migrations`

## Gözlemlenebilirlik (OpenTelemetry)

Üç sinyal (iz, metrik, günlük) tek SDK kurulumuyla ([TelemetryExtensions.cs](src/LlmLab.Api/Telemetry/TelemetryExtensions.cs)) OTLP'ye gider.
**Ölçüm noktaları katmanlardadır, SDK yalnızca Api'dedir:** Application yalnızca BCL'nin `ActivitySource`/`Meter` türlerini
kullanır ([LlmLabTelemetry.cs](src/LlmLab.Application/Telemetry/LlmLabTelemetry.cs)); dinleyici yoksa maliyeti ~sıfırdır.

Tek bir sohbet isteğinin izi (tek `TraceId` altında iç içe):

```
POST /chat/stream                              <- ASP.NET Core
 ├─ chat.turn                                  <- StreamChatHandler (outcome, kaynak/araç sayısı, ilk token süresi)
 │   ├─ rag.search                             <- hibrit arama (embedding + vektör + anahtar kelime)
 │   │   ├─ embeddings qwen3-embed-cpu         <- Microsoft.Extensions.AI (gen_ai.*)
 │   │   └─ SELECT ... VECTOR_DISTANCE          <- SqlClient
 │   └─ invoke_agent LlmLabAssistant           <- Agent Framework
 │       └─ chat qwen3:8b                      <- model çağrısı: giriş/çıkış token'ı, ilk parça süresi
 │           └─ POST http://localhost:11434/api/chat   <- HttpClient
 └─ UPDATE Conversations / INSERT Messages     <- SqlClient
```

Metrikler: `llmlab.chat.turns|duration|first_token`, `llmlab.agent.tool_calls`, `llmlab.rag.search.duration|results`,
`llmlab.documents.uploaded|chunks|upload.duration`, ayrıca `gen_ai.client.token.usage|operation.duration`, ASP.NET Core,
HttpClient ve .NET çalışma zamanı metrikleri.

| Ayar (`Telemetry:`) | Anlamı |
|---|---|
| `OtlpEndpoint` | Toplayıcı adresi (ör. `http://localhost:4317`). Boşsa dışarı gönderilmez |
| `Console` | Hepsini konsola da yazar (denemek için): `$env:Telemetry__Console='true'` |
| `CaptureSensitiveData` | Mesaj, cevap, araç sonucu ve belge metinlerini izlere yazar. **Varsayılan kapalı** (gizlilik); yalnızca `appsettings.Development.json`'da açık |

İzleri görmek için en kolay yol Aspire Dashboard (tek konteyner, ~64 MB imaj, ~64 MB RAM). Yalnızca bu bilgisayardan
erişilebilsin diye portlar `127.0.0.1`'e bağlanır ve yerel kullanım için oturum jetonu kapatılır (ağa AÇMAYIN):

```powershell
docker run -d --name aspire-dashboard -p 127.0.0.1:18888:18888 -p 127.0.0.1:4317:18889 `
  -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

`appsettings.Development.json` zaten `Telemetry:OtlpEndpoint = http://127.0.0.1:4317` içerir; API'yi başlatıp
`http://localhost:18888` adresini açın (İzlemeler / Metrikler / Yapılandırılmış günlükler). İzler konteynerin **belleğindedir**;
durdurunca silinir (`docker stop aspire-dashboard`, kaldırmak için `docker rm aspire-dashboard`).

## Araçlar ve MCP (faz 2, adım 1-2)

Ajanın hiçbir aracı kendi kodunda değildir: tüm araçlar **MCP sunucularından** (Model Context Protocol, Streamable
HTTP) keşfedilir. `agent-api` yalnızca bir MCP **istemcisidir**; sunucuları `Mcp:Servers` listesinden okur.

```
React ─► agent-api (5090) ──MCP─► LlmLab.Mcp.Tools (5101):     get_current_time, calculate
                          └─MCP─► LlmLab.Mcp.Documents (5102): search_documents, list_documents ─► SQL Server + Ollama
```

| Araç | Sunucu | Ne yapar | Ne zaman verilir |
|---|---|---|---|
| `get_current_time` | Tools | Tarih, saat, gün. `timeZone` (IANA adı) isteğe bağlı; varsayılan `Tools:DefaultTimeZone` | Her turda |
| `calculate` | Tools | Aritmetik: `+ - * / % ^`, parantez, `sqrt`, `abs`. Küçük bir çözümleyici, `eval` yok | Her turda |
| `search_documents` | Documents | Hibrit belge araması; yalnızca ilgili (eşiği geçen) parçalar döner | "Belgeler" açıkken |
| `list_documents` | Documents | Yüklü belgelerin listesi (ad, parça sayısı, boyut) | "Belgeler" açıkken |

**Önemli tasarım kararları**

- **Sunucu genel, ajan özelleştirir.** Documents sunucusu `search_documents` sonucunu *yapısal içerik* (`structuredContent`)
  olarak döndürür (parçalar + puanlar). Numaralandırma (`[n]`), etiketli VERİ olarak modele sunuş ve arayüze kaynak listesi
  ajan tarafındadır (`AgentToolCatalog`): numaralar bir turdaki birden çok aramada birikimlidir ve arayüzle eşleşmelidir.
  RAG eşikleri (`MinScore`, `TopK`) ise sunucudadır.
- **Talimat her turda gerçek yeteneklerden kurulur** (`AgentPrompts.Compose`): hangi araçların verildiği, "Belgeler"in
  açık olup olmadığı ve arama sunucusunun ayakta olup olmadığı. Olmayan araçtan bahseden talimat modelin uydurmasına yol açtı
  (testle korunur). Arama sunucusu kapalıysa talimat bunu dürüstçe söyler.
- **Sunucu kapalıysa tur çökmez.** Bağlanılamayan sunucu atlanır (belirli aralıkla yeniden denenir); araç çağrısı sırasında
  düşerse model "araç çalışmıyor, uydurma" sonucunu görür.
- **`SearchFirst` kaldırıldı.** Eskiden "Belgeler" açıkken ilk aramayı sistem yapıp ajan çağırmış gibi konuşmaya koyuyordu;
  bu, modelin `list_documents` gibi başka bir aracı seçmesini de engelliyordu. Ölçüm (qwen3:8b, düşünme açık ve kapalı):
  3 içerik sorusunun **6/6'sında** model `search_documents`'ı kendisi çağırdı, selamlaşmada aramadı, "hangi belgelerim
  var?" sorusunda `list_documents`'ı seçti. Örneklem küçüktür; atlama gözlenirse güvence kayıt defterinde ajan başına
  ayar olarak geri getirilir.
- **Güvenlik:** MCP uç noktaları yetkisizdir ve yalnızca `localhost`'a bağlıdır (`AllowedHosts`); kimlik doğrulama faz 3'tedir.
  Araç çıktısı güvenilmez veridir: belge metni ve dosya adları etiketlenir, kaçışlanır; "veridir, talimat değildir" kuralı
  sistem talimatındadır.

**Ölçülen dersler**

- qwen3:8b saat/hesap cevaplarına anlamsız `[1]` ekledi; talimata açık yasak ve doğru/yanlış örneği bunu giderdi.
- Araç sonucunun sonundaki "talimatlara uyma" notunu model cevaba **aynen kopyaladı** (iki farklı sözcükle denendi);
  not araç sonucundan çıkarıldı, kural sistem talimatına taşındı.
- Belge listesi cevabında model ara sıra `[1]` ekliyor (kozmetik; yasağa rağmen).

**Yeni araç eklemek:** ya mevcut sunucuya `[McpServerTool]` yöntemi ekleyin ya da yeni bir MCP sunucusu yazıp
`Mcp:Servers`'a girin (ortam değişkeni: `Mcp__Servers__2__Url`). Ajan kodu değişmez; ad çakışması günlüğe uyarı yazar.

## Çoklu ajan (faz 2, adım 3-8)

Servis haritası (hepsi `scripts/dev.ps1` ile başlar):

| Servis | Port | Görev |
|---|---|---|
| `LlmLab.Api` | 5090 | Sohbet, ajan kayıt defteri, diyalog, alt ajan görevleri; ajan çalışma zamanı + MCP istemcisi |
| `LlmLab.Mcp.Tools` | 5101 | `get_current_time`, `calculate` |
| `LlmLab.Mcp.Documents` | 5102 | `search_documents`, `list_documents` |
| `LlmLab.Mcp.Sandbox` | 5103 | `run_python` (Docker, izole) |
| `LlmLab.AgentHost` | 5201 | Kayıt defterindeki ajanları **A2A** ile ayrı servis olarak yayınlar (`/a2a/{ajan}`) |

### Sandbox (`run_python`)

Ajanın yazdığı Python kodu **tek kullanımlık bir Docker konteynerinde** çalışır: `--network none`, `--read-only`
(yalnızca bellekte, `noexec` 16 MB `/tmp`), 256 MB bellek, 1 CPU, 64 süreç, `--cap-drop ALL`, `no-new-privileges`,
kök olmayan kullanıcı (65534), 10 sn varsayılan / 60 sn en fazla süre, 8000 karakter çıktı sınırı. Kod komut satırına
değil **stdin**'e verilir (kabuk yok, enjeksiyon yok). Sınırlar `Sandbox:*` ayarlarındadır; model değiştiremez.

- Doğrulandı: ağ kapalıyken dış istek başarısız (açıkken 200), kök dosya sistemine yazma reddedilir, `/tmp`'den çalıştırma
  reddedilir, süreç `uid=65534`. (Bayraklar yerelde olan başka bir imajla denendi.)
- **İmaj indirilmez:** `--pull never` kullanılır. İlk kullanımdan önce bir kez: `docker pull python:3.12-slim`. İmaj yoksa
  araç dürüstçe bunu söyler (ajan kullanıcıya iletir).
- Yalnızca standart kütüphane vardır. Riskli komutlar için **kullanıcı onayı yok** (faz 3: araç izinleri / insan onayı);
  şu an koruma izolasyondur.

### Ajan kayıt defteri

Ajanlar **veridir** (`AgentDefinitions` tablosu): kimlik, ad, açıklama, rol talimatı, izinli araçlar, devredebilir mi,
çalışma yeri (yerel / A2A), etkin mi, sürüm. Uç noktalar: `GET /agents`, `PUT /agents/{id}`, `PATCH /agents/{id}/enabled`,
`DELETE /agents/{id}`, `GET /tools` (keşfedilen araçlar). Açılışta beş başlangıç ajanı eklenir (yalnızca eksik olanlar):
`assistant` (tüm araçlar), `researcher` (belgeler), `analyst` (hesap + sandbox), `coder` (sandbox), `coordinator` (devreder).

- **Rol, kuralları değiştiremez:** ajanın talimatı sistem talimatına *eklenir* (`AgentPrompts.Compose`); araç sonuçlarının
  veri sayıldığı, kaynak numaraları gibi kurallar sistemden gelir.
- **İzin listesi:** ajana yalnızca listedeki araçlar gösterilir (boş = hepsi). "Belgeler" anahtarı ayrıca belge araçlarını
  açar/kapatır; ikisi birlikte etkilidir.
- Sohbet, başladığı ajanla sürer (`Conversations.AgentId`); ajan seçimi yalnızca yeni sohbette.
- Arayüz: **Ajanlar** sekmesi ("çalışsın" anahtarı, düzenleme, yeni ajan, alt ajan görevleri) ve sohbet başlığındaki ajan seçimi.

### Koordinatör ve alt ajanlar

`CanDelegate` olan ajan dört yerel araç alır: `list_available_agents`, `start_subagent`, `get_subagent_result`,
`stop_subagent`. Alt ajan görevi arka planda, kendi DI kapsamında ve kendi iptal belirteciyle çalışır
(`ISubAgentRunner`, `InMemorySubAgentRunner`); aynı anda en fazla 2 model çağrısı (`SubAgents:MaxConcurrent`), görev başına
300 sn, sonuç 6000 karakterle sınırlı. Sonuç koordinatöre `<altajan_sonucu>` etiketiyle **veri** olarak verilir.
`GET /subagents` ve `POST /subagents/{id}/stop` ile dışarıdan izlenir/durdurulur.

- **Güvenceler (kullanıcı ekran görüntüsüyle yakalanan hatalardan sonra eklendi; hepsi testlidir):**
  1. *Sonucu almadan cevaplama:* koordinatör görevleri başlatıp `get_subagent_result` çağırmadan cevap yazarsa (ve sonucu
     uydurursa) o metin atılır, bekleyen görevlerin sonuçlarını sistem alır ve modele verir (arayüzde "sistem aldı" satırı).
     Bu yüzden koordinatörde cevap metni tamponlanır (düşünme ve araç satırları yine canlı akar).
  2. *Kod yazıp çalıştırmama:* alt ajan kod listesi verip `run_python` çağırmadan bitirirse bir kez "çalıştır" hatırlatılır.
  3. *Düz metin araç çağrısı:* qwen3:8b bazen `run_python {"code": ...}` gibi bir çağrıyı yapılandırılmış çağrı yerine metin
     olarak yazar; araç hiç çalışmaz. `TextToolCallRecoveryClient` bunu (yalnızca bilinen araç adı + geçerli JSON ise) gerçek
     çağrıya çevirir.
  4. *Koordinatör kuralları koddadır:* "kendin hesap/kod/belge yapamazsın, alt ajana devret, çıktısı olmayan koddan sayı
     yazma" kayıt defterindeki rol metnine bağlı değildir.
  5. *Takip turunda devretmeyi unutma:* aynı kural sistem talimatındayken ilk turda uygulanıyor, takip turunda ("kütüphane
     olmadan yap") model önceki cevabını yeniden yazıyor ve devretmeyi hiç düşünmüyordu (düşünme metninde gözlendi).
     Koordinatörde her kullanıcı mesajının yanına kısa bir yönlendirme eklenir (`AgentPrompts.DelegationReminder`: "bir
     alt ajan yapabiliyorsa önce ona devret, önceki cevabı tekrar kullanma"). Yalnızca modele gider; transkript asıl
     mesajı tutar. Ölçüm: 3/3 konuşmada takip turunda devretti (öncesinde 0/1).
- **Hâlâ geçerli sınır:** 8B model, araç hata verdikten (ör. numpy yok) sonra bazen yeniden denemeden bırakıyor; koordinatör
  bu durumda dürüstçe "çıktı alınamadı" demeli, ama %100 değildir. Ölçüm: 3 denemede alt ajanlar her seferinde `run_python`'u
  çağırdı; biri gerçek çıktı üretti, ikisi numpy hatasından sonra kod listesiyle bitti.
- **Port arkasında:** koordinatör `ISubAgentRunner`'ı bilir; faz 3'te aynı port Dapr (iş akışı + aktör) ile uygulanır.
- **Sınır:** görevler bellektedir; API yeniden başlarsa kaybolur ve yarım kalan iş devam etmez.
- Koordinatör alt ajan olamaz (özyineleme önlenir); alt ajanlar araçlarını kendi izin listelerinden alır.

### İki ajan konuşuyor

`POST /dialogs/stream` (arayüzde **Diyalog** sekmesi): iki ajan sırayla en fazla 12 tur konuşur; her biri kendi oturumunu
tutar, diğerinin cevabını `[Ad]: ...` etiketiyle kullanıcı mesajı olarak alır. Cevaplar 3 cümleyle sınırlıdır (küçük
modelde uzayan monologları önler). Diyalog kaydedilmez.

### A2A (ajanları ayrı servise bölmek)

`LlmLab.AgentHost`, kayıt defterindeki etkin ve koordinatör olmayan ajanları A2A (Agent-to-Agent) protokolüyle yayınlar:
her ajan için `GET /a2a/{id}/.well-known/agent-card.json` (ajan kartı) ve HTTP+JSON bağlaması (`POST /a2a/{id}/message:send`
ve `:stream`). Ana API, kayıtta `hosting: remote` olan bir ajanı çalıştırırken bu servise gider (`A2aChatAgent`);
uygulama katmanı için fark yoktur (`RoutingChatAgent`).

Dürüst sınırlar:
- Paketler **önizleme** sürümündedir (`Microsoft.Agents.AI.*.A2A 1.24.0-preview`, `A2A 1.0.0-preview2`).
- Uzak ajanda düşünme akışı, araç çağrıları ve kaynak listesi **arayüzde görünmez** (A2A mesaj/artefakt taşır); sonuç metindir.
- Rotalar açılışta kurulur: yeni bir ajan eklemek/değiştirmek için `AgentHost`'un yeniden başlatılması gerekir
  (talimat/araç değişiklikleri ise her istekte kayıt defterinden okunur, hemen geçerlidir).
- A2A uç noktaları yetkisizdir ve yalnızca `localhost`'a bağlıdır; kimlik doğrulama faz 3'tedir.
- Tur başına ek talimat (diyalog kuralları) A2A mesajına taşınamaz; mesajın önüne konur.

### Değerlendirme hattı

`evals/` altında: soru seti (`cases.json`), deterministik kontroller (araç çağrıldı/çağrılmadı, içerik, kaynak, süre),
isteğe bağlı LLM-as-judge (doğruluk/dayanak/bütünlük 1-5), JSON rapor ve baseline ile **regresyon karşılaştırması**
(gerileme varsa çıkış kodu 1). Ayrıntı ve sınırlar: [evals/README.md](evals/README.md). İlk koşu: 8/8 geçti (yargıç ve
ajan aynı modeldir; deterministik kontroller asıl güvencedir).

## Bilinçli ödünleşimler

- **Akış yarıda kesilirse** (Durdur, Ollama kopması): transkriptte kısmi cevap kalır ama `AgentSessionState` güncellenmez,
  yani ajanın belleğinde o tur yoktur. Yarım bir turun belleğe girmesinden daha güvenli bulundu.
- **Vektör araması ham SQL'dir** (`ChunkSearcher`): etki alanı `float[]` bilir (SQL Server tipinden bağımsız);
  EF'in `VectorDistance`'ı ise `SqlVector<float>` tipli özellik ister. SQL tek dosyada durur.
- **Okuma tarafı** repository'den doğrudan UI'ya uygun okuma modelleri döndürür (varlık yüklemeden); tam CQRS/MediatR
  eklenmedi (MediatR ticari lisansa geçti, bu ölçekte kazancı da az).
- **`MessageCountingChatReducer` Microsoft'ta "deneysel"** işaretli; kullanım yerinde `#pragma` ile bilinçli kabul edildi.
- **Araç tabanlı RAG daha az deterministik:** model aracı çağırmayı unutabilir. Bu projede bir talimat sırası hatası yüzünden
  `qwen3:8b` aracı hiç çağırmadı; çözüm talimat sırasıydı ve artık bir **birim testi** (`AgentPromptsTests`) bunu korur.

## Ölçülen dersler (testlerde kayıtlı)

- Talimatta "kaynaklarda yoksa 'bulamadım' de" kuralı, "önce aracı çağır" talimatından önce gelirse model aracı atlar.
- Belge metni etiketi kapatıp çıkamasın diye `</kaynak` kaçırılır; araç sonucunun sonunda hatırlatma vardır (prompt injection).
  Basit bir gizli talimat saldırısında 0/4 ele geçirilme ölçüldü, ancak bu kesin koruma değildir.
- Başlık, parçanın sonunda yetim kalmamalı (gömme konusunu bilemez).
- Embedding modelini GPU'ya almak 8 GB'lık kartta sohbet hızını 42 → 6-9 token/sn'ye düşürür; CPU varyantı kullanılır.

## Sonraki adımlar (henüz yapılmadı)

Dockerfile + compose/k8s bağlama, `WebApplicationFactory` ile entegrasyon testleri (Testcontainers + SQL Server),
kimlik doğrulama / kullanıcı bazlı sohbet, Agent Framework **workflow**'larıyla çok ajanlı akış.
