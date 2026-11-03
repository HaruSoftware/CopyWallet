# Wallet Copy

Aplicativo desktop WPF para acompanhar carteiras públicas e posições da Polymarket. A interface é apenas de leitura: não solicita chaves privadas e não envia, altera ou cancela ordens.

A interface usa [ModernWPF da Kinnara](https://github.com/Kinnara/ModernWpf), com tema Fluent claro/escuro, janela moderna e placeholders acessíveis. As páginas **Visão geral**, **Carteiras**, **Mercados**, **Trades copiados** e **Configurações** são navegadas separadamente. Notificações popup apresentam confirmações, avisos e erros e desaparecem automaticamente.

A aba **Configurações** permite escolher o tema **Sistema**, **Claro** ou **Escuro**. A escolha é aplicada à interface e salva localmente em `%APPDATA%\WalletCopy\settings.json`.

A aba **Trades copiados** funciona atualmente como uma simulação local. Cada trade da atividade pode ser marcado individualmente, uma carteira pode marcar todos os trades atuais e a opção **Novos** pode registrar automaticamente os trades futuros carregados para aquela carteira. Trades já registrados são bloqueados por uma chave local estável e o histórico, junto com as carteiras em cópia automática, é salvo em `%APPDATA%\WalletCopy\settings.json`. Nenhuma ordem é enviada nesta etapa.

Ao marcar um trade individualmente, o aplicativo abre uma confirmação com os modos **Automático** (posição, preço e tamanho observados) e **Manual** (posição Up/Down, tamanho em shares e preço por share). Os dados escolhidos são registrados no histórico local para preparar a futura integração de execução.

O rascunho de ordem também preserva os identificadores públicos do ativo (`token_id` e `condition_id`), o lado, tipo de ordem (FOK, FAK, GTC ou GTD) e slippage máximo. Esses campos são preparação estrutural; assinatura, chave privada e envio de ordens permanecem desativados até uma etapa explícita de execução segura.

O **Terminal simulado** usa os mercados públicos carregados da Gamma API, mas opera uma conta virtual local com saldo inicial de $10.000. Ele permite selecionar mercado e outcome, informar shares, preço, tipo de ordem e slippage, além de simular split de dois outcomes. O serviço aplica limite por trade, limite de exposição, saldo virtual, fills parciais para GTC e registro de auditoria. A conta e as ordens são salvas localmente em `%APPDATA%\WalletCopy\settings.json`; nenhuma transação on-chain ou ordem da Polymarket é criada.

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
