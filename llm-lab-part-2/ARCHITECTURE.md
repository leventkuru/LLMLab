# llm-lab mimarisi

Yerel bir LLM ile konuşan, belgelerinizden cevap bulabilen bir sohbet uygulaması. İki API sürümü yan yana durur
(karşılaştırma için), ikisi de aynı React arayüzüne ve aynı HTTP sözleşmesine hizmet eder.

## 1. Büyük resim

```
 Tarayıcı ─────────► React (web/)  ──── /chat/stream (SSE), /conversations, /documents, /search ────┐
                                                                                                    ▼
                                   ┌───────────────────────────────────────────────────────────────────────────┐
                                   │  API (iki sürüm, aynı sözleşme)                                           │
                                   │   • agent-api/  : .NET, Microsoft Agent Framework, katmanlı mimari  (5090)│
                                   │   • LlmApi/     : .NET, tek proje, elle yazılmış RAG (kıyas tabanı) (5080)│
                                   └──────┬───────────────────────┬─────────────────────────┬─────────────────┘
                                          │                       │                         │ OTLP (iz, metrik, günlük)
                                          ▼                       ▼                         ▼
                              SQL Server 2025              Ollama (Windows'ta,        Aspire Dashboard
                              sohbet + belgeler +          GPU)                       (Docker, :18888)
                              VECTOR(1024) parçalar        qwen3:8b + embedding
```

| Parça | Nerede | Ne iş yapar |
|---|---|---|
| `web/` | Vite + React + TypeScript | Sohbet, belge yükleme/arama, düşünce ve araç satırları, kaynaklar |
| `agent-api/` | .NET 10, Clean Architecture | Ajan (Agent Framework), RAG, oturum, telemetri. **Asıl mimari budur** |
| `LlmApi/` | .NET 10, tek proje | İlk sürüm. Aynı davranışın elle yazılmış hâli; karşılaştırma için bilerek korunur |
| Ollama | Windows ana makinesinde | Modelleri çalıştırır (GPU erişimi için konteyner/pod dışında) |
| SQL Server 2025 | Ana makinede ya da konteynerde | Tüm kalıcı veri, vektörler dahil |
| `compose.yaml`, `k8s/` | Docker / kind | Eski API + web + SQL'in konteynerleştirilmiş çalışması |
| Aspire Dashboard | Docker | OpenTelemetry izlerini/metriklerini gösterir |

## 2. Bir sohbet isteği baştan sona (agent-api)

1. React `POST /chat/stream {message, think, useDocuments, conversationId}` gönderir; cevap **SSE** olarak akar.
2. `StreamChatHandler` (Application) sohbeti yükler ya da açar, **kullanıcı mesajını hemen kaydeder** (model cevap
   vermese de soru kaybolmasın), `chat.turn` izini başlatır.
3. `IChatAgent` portu üzerinden ajan çalışır (Infrastructure'daki `MafChatAgent`). "Belgeler" açıksa önce **ilk
   arama sistem tarafından yapılır** (aşağıda 4).
4. Ajan Ollama'ya model çağrıları yapar; düşünce, araç çağrısı ve cevap metni **olaylar** olarak akar.
5. Handler olayları SSE'ye çevirir ve biriktirir. Tur bitince (ya da kullanıcı "Durdur"a basınca) **tek işlemde**
   transkripti (mesaj, düşünce, kaynaklar, araç çağrıları + metindeki konumları) ve **ajan oturumunu** kaydeder.

**Katmanlar** (oklar yalnızca içe): `Api → Infrastructure → Application → Domain`. EF Core, Ollama ve Agent Framework
yalnızca Infrastructure'da; Application portları (arayüzleri) bilir. Ayrıntı: `agent-api/README.md`.

## 3. Microsoft Agent Framework nasıl kullanılıyor

Yalnızca `MafChatAgent.cs` içinde; geri kalan kod çatıdan habersiz (`IChatAgent` portu).

- **Ajan:** `ChatClientAgent`, Ollama'nın `IChatClient`'ı üzerinde. Her turda, o turun gerçek yeteneklerine göre derlenmiş
  talimatla kurulur: "Belgeler" açık/kapalı, arama sunucusu ayakta/kapalı (yoksa model olmayan bir araçtan söz edip uyduruyordu).
- **Oturum (`AgentSession`):** Ajanın çalışma belleği (geçmiş mesajlar, araç çağrıları). Tur sonunda JSON'a
  serileştirilip `Conversations.AgentSessionState`'e yazılır, sonraki turda geri yüklenir; API yeniden başlasa da sürer.
  Geçmiş `MessageCountingChatReducer` ile son 20 mesajla sınırlanır.
- **Transkript ≠ oturum:** Kullanıcının gördüğü kayıt (`Messages`) ile ajanın belleği (`AgentSessionState`) bilerek ayrı.
- **Araçlar (MCP):** Ajanın kendi kodunda araç yoktur. Araçlar iki MCP sunucusundan keşfedilir
  (`LlmLab.Mcp.Tools`: saat, hesap; `LlmLab.Mcp.Documents`: belge arama ve listeleme) ve her tur ajana verilir. Belge
  aramasında kaynaklar ajan tarafında numaralanır, `[1]` atıfları arayüzdeki listeyle eşleşir. Talimat her turda
  gerçekten verilen araçlardan kurulur; bir sunucu kapalıysa ajan onsuz çalışır. (Ayrıntı: `agent-api/README.md`.)
- **Ön arama yok:** Eski `Agent:SearchFirst` kaldırıldı; aramayı çağırıp çağırmamaya model karar verir. Ölçüm ve gerekçe
  `agent-api/README.md`'de.
- **Düşünme ve bağlam:** `ChatOptions.RawRepresentationFactory` ile Ollama'nın `think` bayrağı ve `num_ctx` (8192) iletilir.

## 4. RAG (belge arama)

Yükleme: dosya → metin (txt/md/**PdfPig**) → `TextChunker` (başlığı altındaki metne yapıştırır, örtüşmeli, ~600 karakter)
→ her parça `qwen3-embed-cpu` ile 1024 boyutlu vektöre → belge ve parçalar **tek işlemde** SQL'e.

Arama (**hibrit**): (1) sorgu vektörüne en yakın parçalar (`VECTOR_DISTANCE('cosine', …)`), (2) sorgudaki ayırt edici
kelimeleri içeren parçalar (`LIKE`, Türkçe için kaba kök: ilk 5 harf), ikisi birleşir. Eşik: `MinScore 0,42`; kelimeyle
bulunmuşsa `0,30`. Neden hibrit: embedding kısa/genel sorgularda ("aurora nedir") ve özel isimlerde zayıf.

Güvenlik: belge metni modele **etiketli veri** olarak (`<kaynak>…</kaynak>`) verilir, `</kaynak` kaçırılır, sonda
hatırlatma vardır (dolaylı prompt injection; basit saldırıda 0/4 ele geçirme, kesin koruma değildir).

## 5. Ollama

Ana makinede çalışır, çünkü GPU'ya (AMD RX 7600, 8 GB) erişmesi gerekir. Konteyner/pod'lar ona
`host.docker.internal:11434` ile ulaşır.

- `qwen3:8b`: sohbet (≈42 token/sn, 5,8 GB VRAM).
- `qwen3-embed-cpu`: `qwen3-embedding:0.6b` ile **aynı ağırlıklar**, `ollama/Modelfile.qwen3-embed-cpu` ile **CPU'ya**
  sabitlenmiş varyant. Neden: embedding modeli GPU'da yan yana yüklenince 8 GB kart dolar ve sohbet **42 → 6-9 token/sn**'ye
  düşüyordu (üstelik model yeniden yüklenene kadar yavaş kalıyordu). Bedel: arama başına ~0,2 sn.
- Embedding istemci ayarları (CPU/GPU) OllamaSharp'ın embedding yolundan iletilemediği için Modelfile ile çözüldü.

## 6. SQL Server

- **Sürüm:** SQL Server 2025 (yerel `VECTOR` tipi). Geliştirmede ana makinedeki Developer sürümü (Windows kimlik doğrulama,
  veritabanları `DevDb` ve `LlmLabAgent`); konteyner/pod'da `mssql/server:2025-latest` (SQL kimlik doğrulama, parola `.env`'de).
- **Tablolar (agent-api):** `Conversations` (+ `AgentSessionState`), `Messages` (+ `SourcesJson`, `ToolCallsJson`),
  `Documents`, `Chunks` (`Embedding vector(1024)`). Silmeler `ON DELETE CASCADE`.
- **EF Core 10 + migration'lar** Infrastructure'da. Vektör araması **ham SQL** (`ChunkSearcher`): etki alanı düz `float[]`
  bilir; `SqlVector` dönüşümü yalnızca EF eşlemesindedir.
- Embedding modeli değişirse **boyut + migration + belgeleri yeniden gömme** gerekir (farklı modellerin vektörleri karşılaştırılamaz).

## 7. OpenTelemetry

- **SDK yalnızca Api'de** (`TelemetryExtensions`); ölçüm noktaları katmanlarda, .NET'in kendi `ActivitySource`/`Meter`
  türleriyle (Application hiçbir OTel paketine bağımlı değil).
- **Üretenler:** ASP.NET Core, HttpClient (Ollama çağrıları), SqlClient, çalışma zamanı; `chat.turn`, `rag.search`,
  `documents.upload`; Microsoft.Extensions.AI ve Agent Framework'ün `gen_ai.*` aralıkları (model, token, süre) ve `llmlab.*` metrikleri.
- **Bir isteğin izi:** `POST /chat/stream → chat.turn → rag.search → embeddings/SQL`, `→ invoke_agent → chat qwen3:8b → HTTP`.
- **Nereye:** `Telemetry:OtlpEndpoint` (geliştirmede `http://127.0.0.1:4317`) → **Aspire Dashboard** (Docker, `localhost:18888`).
  Boşsa hiçbir yere gitmez; `Telemetry:Console` ile konsola da yazılır.
- **Gizlilik:** mesaj/cevap/belge metni izlere varsayılan **yazılmaz**; yalnızca `appsettings.Development.json`'da açık.

## 8. Docker

- `LlmApi/Dockerfile`: iki aşamalı (SDK ile derle → yalnızca ASP.NET çalışma zamanı), `MigrateOnStartup=true`.
- `web/Dockerfile`: Vite derlemesi → **nginx**. `nginx.conf` `/api/`'yi API'ye yönlendirir (SSE için tamponlama kapalı,
  yükleme için 25 MB), böylece aynı imaj her ortamda çalışır ve CORS gerekmez.
- `compose.yaml`: `mssql` (sağlık kontrolü hazır olana kadar API'yi bekletir) + `llmapi` + `web` (`localhost:8080`).
  SQL lisansı için `.env`'de `ACCEPT_EULA=Y`'yi **siz** yazarsınız.
- **`agent-api/` henüz konteynerleştirilmedi** (Dockerfile yok). Aspire Dashboard yalnızca bir `docker run`.

## 9. Kubernetes (kind)

`kind` ile yerel tek düğümlü küme (`llm-lab`), `k8s/llm-lab.yaml`:
- `mssql`: **StatefulSet** + 2 GB kalıcı disk (PVC); `llmapi` ve `web`: **Deployment** + **Service**.
- `k8s/deploy.ps1`: imajları derler → `kind load docker-image` ile düğüme yükler → Secret'ı `.env`'den
  (`--from-env-file`, parola komut satırında görünmez) oluşturur → manifest uygular.
- API pod'u Ollama'ya `host.docker.internal:11434` ile ulaşır; SQL parolası `$(SA_PASSWORD)` ile bağlantı dizesine girer.
- Erişim `kubectl port-forward` ile (`svc/web → localhost:8081`, `svc/mssql → 127.0.0.1:14330`, SSMS için).
- WSL2 bellek üst sınırı 6 GB (`.wslconfig`); compose ve kind yığınları **aynı anda** çalıştırılmaz.
- **Kümede çalışan şu an eski `LlmApi`'dir;** agent-api'nin Dockerfile ve manifestleri henüz yok.

## 10. Çalışma biçimleri ve portlar

| Biçim | Arayüz | API | Veritabanı |
|---|---|---|---|
| Geliştirme (eski) | `localhost:5173` | 5080 (`LlmApi`) | ana makine `DevDb` |
| Geliştirme (yeni) | `localhost:5174` (`VITE_API_URL=http://localhost:5090`) | 5090 (`agent-api`) | ana makine `LlmLabAgent` |
| Docker Compose | `localhost:8080` | konteyner (eski API) | konteynerde SQL |
| Kubernetes (kind) | `localhost:8081` (port-forward) | pod (eski API) | pod'da SQL + PVC |
| İzleme | `localhost:18888` (Aspire) | — | izler konteyner belleğinde |

## 11. Yolda öğrenilen, ölçülen dersler

- Embedding modeli seçimi RAG'in en kritik kararı: `nomic-embed-text` Türkçede ayırt edici değildi; `qwen3-embedding` ile
  doğru parça 1. sıraya çıktı.
- İki modeli aynı 8 GB karta yüklemek sohbet hızını 7 kat düşürdü (embedding CPU'ya alındı).
- Talimat sırası davranışı değiştirir: "bulamadım de" kuralı "önce ara"dan önce gelirse model aracı hiç çağırmıyor (birim testli).
- Araç yokken talimatın araçtan söz etmesi modeli uydurtuyor (ayrı talimat).
- Belge içine gizlenmiş talimat: kaynakları sistem mesajına koymak 5/5 ele geçirildi; etiketli veri olarak vermek 0/5.
- Güvenlik denetimi (NuGet audit) derlemeyi durdurur; uyarılar hata sayılır.

## 12. Eksikler

`agent-api` için Dockerfile/k8s manifesti, `WebApplicationFactory` + gerçek SQL ile entegrasyon testleri, kimlik doğrulama,
araç çağrılarının metin konumuna göre doğru yerde görünmesinin otomatik arayüz testi, kalıcı izleme yığını
(Jaeger/Tempo/Prometheus), Agent Framework workflow'larıyla çok ajanlı akış.
