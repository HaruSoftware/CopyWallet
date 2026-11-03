# Wallet Copy

Aplicativo desktop WPF para acompanhar carteiras públicas e posições da Polymarket. A interface é apenas de leitura: não solicita chaves privadas e não envia, altera ou cancela ordens.

A interface usa [ModernWPF da Kinnara](https://github.com/Kinnara/ModernWpf), com tema Fluent escuro, janela moderna e placeholders acessíveis. As páginas **Visão geral**, **Carteiras**, **Mercados** e **Trades copiados** são navegadas separadamente. Notificações popup apresentam confirmações, avisos e erros e desaparecem automaticamente.

## Requisitos

- Windows
- .NET 8 SDK com suporte a WPF

## Executar

Na pasta do projeto:

```powershell
dotnet run
```

Abra **Carteiras** para adicionar o endereço público de um player e selecionar qual carteira exibir. Vincule seu próprio endereço na mesma página, se quiser consultar suas posições pessoais. Ambos são opcionais e ficam salvos localmente. A carteira de demonstração anteriormente pré-carregada é removida automaticamente na atualização; informe agora o endereço que deseja acompanhar.

Na carteira em foco, a aba **Activity** acompanha trades executados (compras e vendas, mesmo que a posição já tenha sido encerrada), com layout inspirado na Polymarket: lado do trade, preço do outcome, shares e valor bruto; o valor líquido e a taxa aparecem logo abaixo. A taxa do mercado é considerada quando o trade foi executado como taker; trades maker não têm taxa. A aba **Positions** consulta separadamente as posições abertas que continuam na carteira.

O Dashboard também mostra PnL no período diário da API, PnL total (incluindo posições abertas e rendimentos), valor de mercado das posições e saldo pUSD on-chain do player selecionado. O valor das posições não representa o saldo disponível para negociação.

## Dados

- Trades, posições e desempenho: Polymarket Data API v2 (`/v2/trades`, `/v2/positions`, `/v2/user-pnl`, `/v2/user-stats` e `/v2/value`); saldo pUSD consultado por leitura pública `balanceOf` na Polygon; parâmetros de taxa por mercado: Gamma API
- Mercados em destaque e imagens: Polymarket Gamma API (`/events`)
- Atualização automática a cada 15 segundos; a API pública de carteira é consultada periodicamente, sem autenticação ou websocket privado.

O painel **Área de trades copiados** é apenas um espaço reservado para desenvolvimento futuro. Nenhuma ordem é copiada ou enviada.
