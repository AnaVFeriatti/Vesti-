# 🌸 Vestiê

App web em C# (ASP.NET Core 10) que guarda as roupas do seu guarda-roupa e monta looks criativos
de acordo com **o clima da sua cidade** e **a ocasião** (trabalho, festa, academia, encontro...).
Também tem um **dashboard de analytics** sobre como você usa suas roupas.

## Como rodar (Docker)

Pré-requisito: Docker com Compose. No Linux, use `sudo` se o seu usuário não estiver no grupo `docker`.

```bash
docker compose up -d --build
```

Abra **http://localhost:8081**.

O Compose sobe três serviços:

| Serviço | Imagem | Função | Porta no seu PC |
|---|---|---|---|
| `web` | `Dockerfile` (.NET 10) | O app | `8081` (mude com `WEB_PORT`) |
| `db` | `postgres:16-alpine` | Banco de dados | `5433` (mude com `DB_PORT`) |
| `rembg` | `danielgatis/rembg:2.0.87` | IA local que remove o fundo das fotos | nenhuma, só o app acessa |

- Para testar sem cadastrar nada: **Sobre Mim → Carregar exemplo** (27 peças + ~45 dias de histórico fictício).
  Depois é só clicar em **Remover exemplo**.
- Ver os logs do app: `docker compose logs -f web`.
- Parar: `docker compose down` (os dados ficam salvos nos volumes `pgdata`, `uploads` e `rembg-models`).
- Apagar tudo: `docker compose down -v`.

## Como rodar sem Docker (desenvolvimento)

Com o [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) instalado:

```bash
cd src/MontarLooks.Web
dotnet run
```

Sem a connection string do Postgres, o app usa **SQLite** em `src/MontarLooks.Web/data/montarlooks.db`.
Sem `RemoveBg__Url`, as fotos usam só o recorte automático, sem a IA de remoção de fundo.

Na raiz do repositório:

```bash
dotnet build
dotnet test
```

## Funcionalidades

| Tela | O que faz |
|---|---|
| **Meu Ateliê** (look do dia) | Clima atual (Open-Meteo, sem chave), ocasião e 3 looks diferentes vestidos num **provador** (avatar ou foto de corpo inteiro), com o "por que combina". **Amei / Nunca mais** ensinam o motor; **Não sei o que vestir** sorteia na roleta; "Vou usar este" salva no histórico e pode mandar a blusa para o Cesto de Roupa. |
| **Me Surpreenda** | Abre o Meu Ateliê com um sorteio novo e a roleta girando sozinha: um look pronto sem precisar escolher nada. |
| **Meu Closet** (guarda-roupa) | Peças com foto, categoria, tipo, cores, estilo, calor, formalidade, chuva, preço e detalhes (manga/alça, decote, comprimento, caimento, estampa, tecido, cintura). As fotos são tratadas por uma IA local (rembg): fundo removido, peça de pé, sem cabide. Na edição dá para girar, retocar de novo ou usar a foto original. O envio de várias fotos roda em segundo plano, com barra de progresso, e só aceita arquivos que sejam mesmo imagens. |
| **Agenda Fashion** (semana) | Planeja segunda a sexta com a previsão de cada dia e a agenda ("reunião", "academia", "jantar"...), sem repetir peça principal. |
| **Cesto de Roupa** (lavanderia) | Peça suja fica no cesto e sai das sugestões até ser marcada como lavada. |
| **Mais Mimos** | Mala de viagem, Cápsula e lacunas ("uma calça bege renderia mais 21 looks"), Lista de desejos, Desapego (6 meses sem uso), Paleta pessoal (coloração em 12 estações, com teste de cores na foto), Retrospectiva do mês, Histórico (CSV) e Estatísticas. |
| **Sobre Mim** (configurações) | Provador (tom de pele, cabelo, fotos), "começo a sentir frio abaixo de X°C", regras por ocasião (ex.: trabalho sem alça, decote, saia ou sapato aberto), cidade, retoque de todas as fotos em segundo plano e dados de exemplo. |

## Como o gerador de looks funciona

`src/MontarLooks.Web/Services/OutfitGenerator.cs` coordena a busca e, com as classes `Outfit*` da mesma pasta, monta as combinações (parte de cima + de baixo ou peça única,
camada opcional como cardigan, casaco, calçado e acessório) e dá uma nota para cada uma:

1. **Regras da ocasião** (filtro duro): o que você marcou como proibido nunca aparece.
2. **Temperatura pessoal**: usa a sensação térmica ajustada pelo seu "sinto frio abaixo de"; com manhã fria
   e tarde quente sugere uma camada removível. Calçados seguem a temperatura real: com 24°C ou mais a bota só entra se não houver outro calçado.
3. **Ocasião**: formalidade e estilo esperados.
4. **Cores**: neutros, ponto de cor, contraste claro-escuro, análogas, complementares, tom sobre tom e a sua paleta pessoal perto do rosto.
5. **Proporção e detalhes**: volume em cima ou embaixo, cropped com cintura alta, uma estampa protagonista, tecido x clima.
6. **Seu gosto**: combinações curtidas ganham pontos; "nunca mais" não volta.
7. **Variedade**: evita peças recém-usadas, peças no Cesto de Roupa e repetir peça principal ou calçado entre as sugestões.

## Estrutura

```
src/MontarLooks.Web/
  Models/      ClothingItem, Outfit, paleta de cores, enums (categoria, estilo, ocasião)
  Data/        AppDbContext (EF Core: PostgreSQL no Docker, SQLite local)
  Services/    regras do app (lista abaixo)
  Pages/       Razor Pages (Meu Ateliê, Meu Closet, Agenda Fashion, Cesto de Roupa, Sobre Mim, Mais Mimos)
tests/MontarLooks.Tests/   testes de unidade e de integração das páginas (xUnit)
```

Serviços em `src/MontarLooks.Web/Services/`:

| Área | Classes |
|---|---|
| Looks | `OutfitGenerator`, `OutfitModels`, `OutfitRules`, `OutfitCandidates`, `OutfitScoring`, `OutfitColors`, `OutfitWording`, `OutfitTaste` |
| Tratamento de fotos | `PhotoProcessor`, `PhotoComposer`, `PhotoRetouch`, `PhotoManualTools`, `PhotoMaskAnalyzer`, `PhotoMaskOps`, `PhotoForeground`, `PhotoOrientation`, `HangerDetector` |
| Arquivos e lotes de fotos | `PhotoStorage`, `PhotoBatchQueue`, `PhotoBatchWorker` |
| Recursos do app | `AnalyticsService`, `CapsuleService`, `PaletteService`, `PlannerService`, `TravelService`, `WishlistService`, `WrappedService` |
| Infraestrutura | `ClosetBackupService`, `DemoDataService`, `JsonStore`, `LocalClock`, `PortugueseValidation`, `SettingsService`, `WeatherService` |

## Analisando os dados por fora

O Postgres fica exposto em `localhost:5433` (mude com a variável `DB_PORT`) (banco `montarlooks`, usuário `looks`, senha `looks`),
então dá para conectar Power BI, Metabase, DBeaver ou um notebook Python. Tabelas principais:
`ClothingItems`, `Outfits` e `OutfitItems` (quais peças estavam em cada look).

> Troque a senha criando um arquivo `.env` com `POSTGRES_PASSWORD=suasenha` antes do primeiro `docker compose up`.
