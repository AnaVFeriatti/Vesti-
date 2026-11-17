# Vestiê · quadro de tarefas (madrugada de 16/11/2026)

Cada agente só mexe nos arquivos da sua coluna "Dono de". A integração, os commits (como Ana Feriatti)
e o resumo final ficam com o agente principal. Nada de sudo, docker ou push durante a noite.

| # | Tarefa | Agente | Dono de | Status |
|---|---|---|---|---|
| 1 | Limpar à mão as fotos com cabide / tortas (12 peças) | principal | dados do app | feito (2 ainda tortas: saia de tweed, blusa com babados) |
| 2 | Ferramentas de foto na edição: girar ↺ ↻, retocar de novo, voltar ao original | fotos | Pages/Wardrobe/*, Services/PhotoStorage.cs, Services/PhotoProcessor.cs | feito |
| 3 | Polimento visual: celular, acessibilidade, estados vazios, toasts, cartão de look | ui | Pages/Shared/*, wwwroot/css/site.css, wwwroot/js/site.js, Pages/Index.cshtml, Pages/Settings.cshtml, Pages/History.cshtml, Pages/Dashboard.cshtml | feito |
| 4 | Motor de looks: camadas, mín/máx do dia, explicações melhores + testes de integração de todas as páginas | motor | Services/OutfitGenerator.cs, Models/StylePreferences.cs, Models/ItemDetails.cs, Pages/Index.cshtml.cs, tests/** (novos arquivos) | feito |
| 5 | Revisão das features novas (bugs, consistência, lavanderia/paleta onde fizer sentido) | qa | Pages/{Planner,Travel,Capsule,Wishlist,Declutter,Wrapped,Palette,Laundry}.*, Services/{Planner,Travel,Capsule,Wishlist,Wrapped,Palette}Service.cs, wwwroot/css/{planner,insights,palette}.css, wwwroot/js/palette.js | feito |
| 6 | Integrar, rodar todos os testes, conferir na tela, commit como Ana, resumo para a manhã | principal | — | feito |

## Rodada de 17/11/2026 (madrugada)

| # | Tarefa | Status |
|---|---|---|
| 7 | Regra da bota coberta por teste: com temperatura real ≥ 24°C a bota sai das sugestões quando há outro calçado (antes, a variação de calçado podia trazê-la de volta) | feito |
| 8 | Menu com os nomes novos: Meu Ateliê (look do dia/provador), Meu Closet (guarda-roupa), Agenda Fashion (semana), Me Surpreenda (look sorteado, roleta gira sozinha), Cesto de Roupa (lavanderia), Sobre Mim (configurações). Títulos, links e README acompanham | feito |
| 9 | Fotos ainda tortas: saia de tweed pink e blusa preta com babados | feito (girada e limpa pela tela de edição) |
| 10 | Tudo em português: "Ferramentas" → Mais Mimos, Wrapped → Retrospectiva do mês, Analytics → Estatísticas, Streetwear → Urbano (nomes escolhidos pelo Hoffmann ficam) | feito |
