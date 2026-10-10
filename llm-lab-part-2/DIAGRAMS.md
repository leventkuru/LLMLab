# Mimari diyagramlar (faz faz)

Her faz ayrı bir diyagramdır. Diyagramlar Mermaid ile yazıldı (GitHub, VS Code "Markdown Preview Mermaid Support" eklentisi ve çoğu Markdown görüntüleyicide çizilir).
Kapsam ve gerekçeler için bkz. [ROADMAP.md](ROADMAP.md), çalışan yapının anlatımı için [ARCHITECTURE.md](ARCHITECTURE.md).

Okuma notu: ok yönü "çağırır / veri gönderir" demektir. Faz 2-4 henüz planlıdır; kutulardaki bazı seçimler varsayımdır (ROADMAP'te "(varsayım)" ile işaretli).

## Faz 1: mevcut yapı (tamam)

```mermaid
flowchart LR
    UI["React arayüzü<br/>Sohbet, Belgeler"]

    subgraph API["agent-api (Clean Architecture)"]
        direction TB
        A["Api<br/>Endpoint, SSE, OTel SDK"]
        B["Application<br/>Handler'lar ve portlar"]
        C["Infrastructure<br/>Agent Framework, EF Core"]
        D["Domain<br/>Konuşma, belge, parça"]
        A --> B
        C --> B
        B --> D
    end

    OLL["Ollama<br/>qwen3:8b, embedding"]
    SQL[("SQL Server<br/>vector(1024)")]
    ASP["Aspire Dashboard<br/>iz ve metrik"]
    OLD["LlmApi (eski)<br/>Docker ve kind'de, kıyas tabanı"]

    UI --> A
    A -- OTLP --> ASP
    C --> OLL
    C --> SQL
    UI -.-> OLD
```

Bağımlılıklar içe doğrudur: Api ve Infrastructure, Application'a; Application, Domain'e bağlıdır.

## Faz 2: ajan yetenekleri ve birlikte çalışabilirlik

```mermaid
flowchart TB
    REG[("Ajan kayıt defteri<br/>SQL, sürümlü")]
    OLL["Ollama<br/>modeller"]
    EVAL["Değerlendirme hattı<br/>soru seti + LLM-as-judge"]

    subgraph AG["Ajanlar (A2A ile ayrı servisler)"]
        direction TB
        CO["Koordinatör ajan<br/>başlat, sonucu al, durdur"]
        S1["Alt ajan A"]
        S2["Alt ajan B"]
        CO --> S1
        CO --> S2
        S1 <--> S2
    end

    subgraph MCP["MCP sunucuları"]
        direction TB
        T["Araç sunucusu<br/>arama, dosya, hesap"]
        SB["Kod sandbox'ı<br/>Docker, ağ yok, sınırlı"]
    end

    REG --> AG
    OLL --> AG
    AG -- araç çağrısı --> T
    S2 -- kod çalıştır --> SB
    EVAL -- test eder --> AG
```

Ajanlar koddan çıkıp kayıt defterindeki kayıtlardan (talimat, model, izinli araç ve beceri, limitler) kurulur.

## Faz 3: işletme ve dayanıklılık

```mermaid
flowchart LR
    subgraph POD["Pod: ajan servisi + Dapr"]
        direction LR
        DP["Dapr sidecar<br/>workflow, aktör"]
        AP["agent-api<br/>ajan çalışır"]
        DP <--> AP
    end

    SQL[("SQL Server<br/>Dapr durum deposu")]
    RED[("Redis<br/>pub/sub, önbellek")]

    subgraph OBS["Gözlem yığını"]
        direction TB
        COL["OTel Collector"]
        STO["Prometheus, Loki, Tempo<br/>metrik, günlük, iz"]
        GRA["Grafana<br/>panel, alarm, SLO"]
        COL --> STO --> GRA
    end

    SEC["Güvenlik ve işletme<br/>onay, bütçe, kimlik, CI, entegrasyon testi"]

    DP --> SQL
    DP --> RED
    AP -- OTLP --> COL
    POD ~~~ SEC
```

Dapr Workflow uzun işleri yönetir ve ajan bir aktivite olarak çalışır. Aktörler konuşma ve alt ajan görev durumunu tutar.

## Faz 4: durum tabanlı gerçek zamanlı arayüz

```mermaid
flowchart LR
    subgraph UI["React arayüzü"]
        direction TB
        U1["Durum ve onay<br/>ağaç, iptal, onay"]
        U2["Sohbet<br/>token akışı"]
        U3["Ajan seçimi<br/>şunlar çalışsın"]
    end

    subgraph API["agent-api (çok kopya)"]
        direction TB
        HUB["SignalR hub<br/>durum ve olay"]
        SSE["SSE uç noktası<br/>sohbet akışı"]
        RA["Kayıt API'si<br/>ajan listesi, ayar"]
    end

    RED[("Redis<br/>pub/sub, backplane")]
    DAP[("Dapr ve SQL<br/>iş, aktör, kayıt")]

    U1 <--> HUB
    SSE --> U2
    U3 <--> RA
    HUB <--> RED
    RA <--> DAP
    DAP -- olay --> RED
```

Bağlanınca arayüz anlık görüntüyü (snapshot) alır, sonra değişiklikleri izler; bağlantı kopup dönerse kaldığı yerden toparlanır.
