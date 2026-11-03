# Wallet Copy — instruções para agentes

## Objetivo e limites

- Aplicativo desktop Windows em WPF/C# para acompanhar carteiras públicas da Polymarket.
- O produto é somente leitura: não solicite, armazene ou use chaves privadas e não implemente envio, alteração ou cancelamento de ordens sem uma mudança explícita de escopo.
- O painel **Trades copiados** é, por enquanto, apenas uma área reservada.
- Preserve a interface escura inspirada na Polymarket, a navegação por páginas e as notificações popup existentes.

## Tecnologias e arquitetura

- Projeto .NET 8 (`net8.0-windows`), WPF e pacote `ModernWpfUI`.
- `MainWindow.xaml` e `MainWindow.xaml.cs`: navegação, ciclo de atualização (15 segundos), seleção da carteira em foco e coordenação dos carregamentos.
- `Pages/`: páginas WPF. `DashboardPage` mostra desempenho e atividade filtrada; `WalletsPage` mostra as abas Activity e Positions; `MarketsPage` mostra mercados; `CopyTradesPage` é um placeholder.
- `Services/PolymarketApiClient.cs`: integração HTTP com a Data API v2 e Gamma API, além de consulta pública `balanceOf` para pUSD na Polygon.
- `Models/`: modelos de trades, posições, carteiras e desempenho.
- Configurações locais são gerenciadas pelos serviços existentes e ficam em `%APPDATA%\WalletCopy\settings.json`; não as mova para o repositório.

## Dados e semântica

- Use a Polymarket Data API v2 (`/v2/trades`, `/v2/positions`, `/v2/user-pnl`, `/v2/user-stats` e `/v2/value`) e a Gamma API para dados de mercados e parâmetros de taxa. A API v1 antiga pode retornar `410 Gone`.
- Trades representam execuções; posições representam exposição atual. Não trate os dois como a mesma métrica.
- O valor bruto do trade é shares × preço. Para taker, a UI também apresenta o valor líquido após a taxa do mercado; maker não paga taxa. Consulte os parâmetros atuais do mercado, sem codificar uma taxa universal.
- O Dashboard distingue PnL diário, PnL total, valor marcado a mercado das posições e saldo pUSD. O valor das posições não é saldo disponível.
- O saldo pUSD é lido na Polygon pelo contrato `0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB` (6 decimais). Não o confunda com USDC.e nem com o valor de posições.
- Carteiras monitoradas e carteira própria são endereços públicos opcionais. Não inclua carteiras de demonstração ou dados pessoais como padrão.

## Convenções de implementação

- Siga os padrões C# existentes, nullable reference types e `async`/`await`; mantenha a UI responsiva durante consultas de rede.
- Erros de API devem permanecer visíveis por meio do tratamento/notificações existentes; não transforme falhas em dados com aparência de sucesso.
- Mantenha nomes de tipos e propriedades do código em inglês; textos apresentados ao usuário seguem o idioma e o estilo já usados na interface.
- Evite dependências novas quando as bibliotecas do projeto e `HttpClient` forem suficientes.
- Atualize o `README.md` quando uma mudança alterar comportamento, dados consultados ou requisitos para executar o aplicativo.

## Build e validação

Na raiz do projeto:

```powershell
dotnet build .\WalletCopy.csproj --no-restore
dotnet run --project .\WalletCopy.csproj
```

Se `dotnet` não estiver no `PATH`, use `C:\Program Files\dotnet\dotnet.exe`. Não adicione `bin/` ou `obj/` ao Git; consulte `.gitignore`.
