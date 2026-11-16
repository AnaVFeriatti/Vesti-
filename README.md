# 🌸 Vestiê

App web em C# (ASP.NET Core 8) que guarda as roupas do seu guarda-roupa e monta looks criativos
de acordo com **o clima da sua cidade** e **a ocasião** (trabalho, festa, academia, encontro...).
Também tem um **dashboard de analytics** sobre como você usa suas roupas.

## Como rodar (Docker)

Pré-requisito: [Docker Desktop](https://www.docker.com/products/docker-desktop/).

```bash
docker compose up -d --build
```

Abra **http://localhost:8081**.

- Para testar sem cadastrar nada: **Configurações → Carregar exemplo** (27 peças + ~45 dias de histórico fictício).
  Depois é só clicar em **Remover exemplo**.
- Parar: `docker compose down` (os dados ficam salvos nos volumes `pgdata` e `uploads`).
- Apagar tudo: `docker compose down -v`.

## Como rodar sem Docker (desenvolvimento)

Com o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) instalado:

```bash
cd src/MontarLooks.Web
dotnet run
```

Sem a connection string do Postgres, o app usa **SQLite** em `src/MontarLooks.Web/data/montarlooks.db`.

Testes: `dotnet test` na raiz.

## Funcionalidades

| Tela | O que faz |
|---|---|
| **Look do dia** | Busca o clima atual (Open-Meteo, sem chave de API), você escolhe a ocasião e recebe 3 looks diferentes, com explicação de por que combinam. "Vou usar este" salva no histórico. "Outras sugestões" sorteia novas combinações. |
| **Guarda-roupa** | Cadastro com foto, categoria, tipo, cor, estilo, quanto esquenta (1-5), formalidade (1-5), se é boa para chuva e preço. Filtros por categoria/cor/nome. |
| **Enviar várias fotos** | Envia todas as fotos de uma vez; cada uma vira uma peça "a completar", e o app te leva de uma em uma para preencher os detalhes. |
| **Histórico** | Looks usados, com nota de 1 a 5 e **exportação CSV** (uma linha por peça usada, pronta para Excel / Power BI / pandas). |
| **Analytics** | Peças mais/menos usadas, peças esquecidas há 30+ dias, cores do guarda-roupa vs. cores que você mais veste, looks por semana, temperatura média, ocasiões, tipos de combinação de cor e **custo por uso**. |
| **Configurações** | Trocar a cidade (padrão: Ourinhos-SP) e carregar/remover dados de exemplo. |

## Como o gerador de looks funciona

`src/MontarLooks.Web/Services/OutfitGenerator.cs` monta todas as combinações possíveis
(parte de cima + parte de baixo, ou vestido/macacão, + calçado, + casaco opcional, + acessório opcional)
e dá uma nota para cada uma:

1. **Temperatura**: usa a sensação térmica puxada para a mínima do dia e compara com o "calor" das peças.
   Abaixo de 17°C exige casaco; acima de 24°C dispensa (a não ser que chova).
2. **Ocasião**: formalidade e estilo esperados (ex.: trabalho = esporte fino/social; academia = só esportivo).
3. **Harmonia de cores**: monocromático, neutro, ponto de cor, análogo (cores vizinhas) ou complementar
   (opostas no círculo cromático). Combinações com muitas cores ou mais de uma estampa perdem pontos.
4. **Chuva**: prefere calçado/casaco marcado como "bom para chuva".
5. **Variedade**: evita peças usadas nos últimos dias e dá um bônus para peças esquecidas.
6. **Criatividade**: um pouco de aleatoriedade, para cada sugestão ser diferente.

Os 3 looks escolhidos sempre diferem entre si em pelo menos duas peças.

## Estrutura

```
src/MontarLooks.Web/
  Models/      ClothingItem, Outfit, paleta de cores, enums (categoria, estilo, ocasião)
  Data/        AppDbContext (EF Core: PostgreSQL no Docker, SQLite local)
  Services/    OutfitGenerator, WeatherService (Open-Meteo), AnalyticsService, PhotoStorage, DemoData
  Pages/       Razor Pages (Look do dia, Guarda-roupa, Histórico, Analytics, Configurações)
tests/MontarLooks.Tests/   testes do gerador de looks (xUnit)
```

## Analisando os dados por fora

O Postgres fica exposto em `localhost:5433` (mude com a variável `DB_PORT`) (banco `montarlooks`, usuário `looks`, senha `looks`),
então dá para conectar Power BI, Metabase, DBeaver ou um notebook Python. Tabelas principais:
`ClothingItems`, `Outfits` e `OutfitItems` (quais peças estavam em cada look).

> Troque a senha criando um arquivo `.env` com `POSTGRES_PASSWORD=suasenha` antes do primeiro `docker compose up`.
