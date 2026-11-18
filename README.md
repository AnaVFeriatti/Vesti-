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

- Para testar sem cadastrar nada: **Sobre Mim → Carregar exemplo** (27 peças + ~45 dias de histórico fictício).
  Depois é só clicar em **Remover exemplo**.
- Parar: `docker compose down` (os dados ficam salvos nos volumes `pgdata` e `uploads`).
- Apagar tudo: `docker compose down -v`.

## Como rodar sem Docker (desenvolvimento)

Com o [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) instalado:

```bash
cd src/MontarLooks.Web
dotnet run
```

Sem a connection string do Postgres, o app usa **SQLite** em `src/MontarLooks.Web/data/montarlooks.db`.

Testes: `dotnet test` na raiz.

## Funcionalidades

| Tela | O que faz |
|---|---|
| **Meu Ateliê** (look do dia) | Clima atual (Open-Meteo, sem chave), ocasião e 3 looks diferentes vestidos num **provador** (avatar ou foto de corpo inteiro), com o "por que combina". **Amei / Nunca mais** ensinam o motor; **Não sei o que vestir** sorteia na roleta; "Vou usar este" salva no histórico e pode mandar a blusa para o Cesto de Roupa. |
| **Me Surpreenda** | Abre o Meu Ateliê com um sorteio novo e a roleta girando sozinha: um look pronto sem precisar escolher nada. |
| **Meu Closet** (guarda-roupa) | Peças com foto, categoria, tipo, cores, estilo, calor, formalidade, chuva, preço e detalhes (manga/alça, decote, comprimento, caimento, estampa, tecido, cintura). As fotos são tratadas por uma IA local (rembg): fundo removido, peça de pé, sem cabide. Na edição dá para girar, retocar de novo ou usar a foto original. |
| **Agenda Fashion** (semana) | Planeja segunda a sexta com a previsão de cada dia e a agenda ("reunião", "academia", "jantar"...), sem repetir peça principal. |
| **Cesto de Roupa** (lavanderia) | Peça suja fica no cesto e sai das sugestões até ser marcada como lavada. |
| **Mais Mimos** | Mala de viagem, Cápsula e lacunas ("uma calça bege renderia mais 21 looks"), Lista de desejos, Desapego (6 meses sem uso), Paleta pessoal (coloração em 12 estações, com teste de cores na foto), Retrospectiva do mês, Histórico (CSV) e Estatísticas. |
| **Sobre Mim** (configurações) | Provador (tom de pele, cabelo, fotos), "começo a sentir frio abaixo de X°C", regras por ocasião (ex.: trabalho sem alça, decote, saia ou sapato aberto), cidade, retoque de fotos e dados de exemplo. |

## Como o gerador de looks funciona

`src/MontarLooks.Web/Services/OutfitGenerator.cs` monta as combinações (parte de cima + de baixo ou peça única,
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
  Services/    OutfitGenerator, WeatherService (Open-Meteo), AnalyticsService, PhotoStorage, DemoData
  Pages/       Razor Pages (Meu Ateliê, Meu Closet, Agenda Fashion, Cesto de Roupa, Sobre Mim, Mais Mimos)
tests/MontarLooks.Tests/   testes do gerador de looks (xUnit)
```

## Analisando os dados por fora

O Postgres fica exposto em `localhost:5433` (mude com a variável `DB_PORT`) (banco `montarlooks`, usuário `looks`, senha `looks`),
então dá para conectar Power BI, Metabase, DBeaver ou um notebook Python. Tabelas principais:
`ClothingItems`, `Outfits` e `OutfitItems` (quais peças estavam em cada look).

> Troque a senha criando um arquivo `.env` com `POSTGRES_PASSWORD=suasenha` antes do primeiro `docker compose up`.
