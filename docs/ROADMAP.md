# MontarLooks · roadmap de features (fase de ideias)

Cada feature tem um dono (agente) e arquivos próprios, para várias frentes andarem em paralelo
sem conflito. Dados novos ficam em JSON na tabela Settings via `Services/JsonStore.cs`
(sem migração de banco). Menu já aponta para todas as páginas (`Pages/Shared/_Layout.cshtml`).

| Feature | Página | Dono | Status |
|---|---|---|---|
| Campos detalhados (manga, decote, comprimento...) | Wardrobe/Edit | principal | feito |
| Regras por ocasião + "sinto frio abaixo de" | Settings | principal | feito |
| Provador (avatar com o look) | Index, Settings | principal | feito |
| Curtir / nunca mais (motor aprende) | Index | principal | feito |
| Roleta "não sei o que vestir" | Index | principal | feito |
| Lavanderia (peça suja sai das sugestões) | Laundry, Wardrobe | principal | feito |
| Planejador da semana (previsão + agenda) | Planner | agente A | feito |
| Mala de viagem | Travel | agente A | feito |
| Cápsula e lacunas | Capsule | agente B | feito |
| Lista de desejos | Wishlist | agente B | feito |
| Desapego (6 meses sem uso) | Declutter | agente B | feito |
| Wrapped do mês | Wrapped | agente B | feito |
| Paleta pessoal (coloração) | Palette | agente C | feito |
| Fotos: de pé, sem cabide, embelezadas | PhotoProcessor | agente D | feito |
| Validação real das fotos enviadas | Wardrobe/Edit, Wardrobe/Bulk, Settings, Palette | principal | feito |
| Fotos em lote em segundo plano, com progresso | Wardrobe/Bulk, Settings | principal | feito |
| Plataforma .NET 10 | todo o projeto | principal | feito |
| ImageSharp sem alertas de segurança (licença ou outra biblioteca) | Services/Photo* | a definir | pendente |
