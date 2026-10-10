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
| **Belge araması** | "Belgeler" açıksa **her** mesajda zorla yapılır | Ajana **`search_documents` aracı** verilir (agentic RAG). **Ancak ölçüldü:** düşünme açıkken qwen3:8b aracı sık atlıyor ("arama yapacağım" yazıp turu bitiriyor, cevabı uyduruyor). Bu yüzden `Agent:SearchFirst=true` (varsayılan): ilk aramayı sistem yapar, sonucu "ajan çağırmış" gibi konuşmaya koyar; model gerekirse yine arar. Saf agentic davranış için `false` |
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

```powershell
cd C:\dev\llm-lab\agent-api\src\LlmLab.Api
dotnet run --urls http://localhost:5090     # geliştirmede veritabanını (LlmLabAgent) migration kendisi oluşturur
```

İkinci arayüz örneği (aynı `web/` kodu, farklı API'ye bakar; CORS'ta 5174 izinlidir):

```powershell
cd C:\dev\llm-lab\web
$env:VITE_API_URL = 'http://localhost:5090'; npm.cmd run dev -- --port 5174 --strictPort
```

Böylece `http://localhost:5173` (eski) ve `http://localhost:5174` (yeni) yan yana açılıp aynı soru iki sürüme sorulabilir.
Yeni sürüm ek olarak `tool` olayları yayar; arayüz bunları cevabın **metinleri arasında**, tıklanınca açılan bir satır
olarak gösterir ("🔍 Belgelerde arandı"). Araç çağrıları, cevap metnindeki **konumuyla** birlikte transkriptte saklanır
(`Messages.ToolCallsJson`); sohbet yeniden açılınca da aynı yerde görünür. Eski API bu olayı göndermez, onun arayüzü değişmez.

Testler: `dotnet test tests/LlmLab.Application.Tests`
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
