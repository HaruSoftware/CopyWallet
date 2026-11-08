# Wallet Copy

Aplicativo desktop WPF (.NET 8) para monitoramento de carteiras da Polymarket e execução de ordens reais e simuladas (Paper Trading) via Polymarket CLOB.

A interface utiliza [ModernWpfUI](https://github.com/Kinnara/ModernWpf), com temas claro/escuro integrados ao Windows e visual inspirado no terminal da Polymarket. As páginas **Visão geral**, **Carteiras**, **Mercados**, **Terminal simulado**, **Monitor de book**, **Trades copiados** e **Configurações** são navegadas de forma independente com notificações em tempo real.

A página **Análise de carteira** lista as carteiras acompanhadas e a carteira própria, consulta até 30 trades recentes e estima sinais de estilo (como giro rápido, convicção, diversificação ou possível automação). O relatório inclui o valor bruto das compras e estima resultado realizado por FIFO apenas quando encontra compras e vendas pareáveis do mesmo token na amostra; o retorno percentual usa somente o custo pareado, não o aporte total da carteira. Operações iniciadas antes dos 30 trades e taxas indisponíveis podem deixar o resultado incompleto. O período exibido vai do trade mais antigo ao mais recente da amostra. Também permite pesquisar qualquer endereço público sem adicioná-lo nem salvá-lo. Essas classificações são indicativas e não comprovam intenção, automação, margem ou rentabilidade.

---

## Recursos e Funcionalidades

### 1. Execução Real de Ordens (Polymarket CLOB)
- **Integração Oficial com a Polymarket CLOB API (`https://clob.polymarket.com`):**
  - Autenticação Nível 1 (L1) via assinatura criptográfica **EIP-712** (`ClobAuthDomain`) para gerar/derivar credenciais de API.
  - Autenticação Nível 2 (L2) com cabeçalhos HMAC-SHA256 (`POLY_API_KEY`, `POLY_SIGNATURE`, `POLY_TIMESTAMP`, `POLY_PASSPHRASE`, `POLY_ADDRESS`).
  - Assinatura local da ordem (`Order` struct) no padrão da Polymarket para Polygon (Chain ID 137).
  - Envio de ordens com tipos suportados: **FOK** (Fill-Or-Kill), **FAK** (Fill-And-Kill), **GTC** (Good-Til-Cancelled) e **GTD**.
  - Suporte tanto para mercados binários padrão (CTF Exchange `0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E`) quanto NegRisk (`0xC5d563A36AE78145C45a50134d48A1215220f80a`).

### 2. Suporte a Contas por E-mail/Google e MetaMask
- **Contas Criadas por E-mail ou Google na Polymarket:**
  - Suporte à arquitetura de carteira Proxy/Magic Link (`signatureType = 1` ou `2`).
  - Permite configurar o endereço de perfil onde ficam os depósitos (`funder/maker`) e a chave privada do signer exportada pelo site da Polymarket.
- **Contas EOA Diretas (MetaMask, Rabby):**
  - Derivação automática do endereço da carteira a partir da chave privada.

### 3. Segurança de Credenciais com Windows DPAPI
- As chaves privadas e credenciais de API nunca são armazenadas em texto plano nem enviadas para repositórios ou servidores de terceiros.
- Criptografia local de ponta a ponta com **Windows Data Protection API (`System.Security.Cryptography.ProtectedData`)** no perfil do usuário atual (`%APPDATA%\WalletCopy\credentials.dat`).
- Botão de desconexão instantânea para limpar credenciais locais a qualquer momento.

### 4. Cópia Manual com Confirmação e Modo Simulado
- No diálogo **Copiar trade**, o usuário pode escolher entre:
  - **Polymarket Real (CLOB):** Envia a ordem diretamente ao livro da Polymarket com validação de credenciais, cálculo de risco e proteção de slippage máximo.
  - **Simulação (Paper):** Registra a posição localmente sem envolver fundos reais.
- A página **Trades copiados** exibe o histórico detalhado, IDs de ordens da Polymarket, status de preenchimento (`Matched`, `Filled`, `Live`, `Cancelled`) e eventuais erros da exchange.

### 5. Terminal Simulado (Paper Trading)
- Book em tempo real via WebSocket da CLOB.
- Execução virtual com cálculo de PnL realizado/não realizado, taxas e liquidação automática na confirmação dos mercados.

### 6. Monitor de book
- Acompanha mercados cripto binários ativos de 5 minutos, 15 minutos ou 1 hora pelo book público da CLOB.
- Estima a venda de 1 share de cada resultado complementar usando profundidade dos bids e descontando as taxas taker atuais do mercado. A meta líquida é configurável; mercados sem book suficiente ou sem parâmetros de taxa disponíveis não são tratados como oportunidades confirmadas.
- Exibe o valor líquido do par e o excedente sobre $1,00. Esse excedente não considera o custo de aquisição das shares. O monitor é somente leitura e não envia ordens.

---

## Requisitos

- Windows 10/11
- .NET 8 SDK com suporte a Desktop WPF

## Executar

Na raiz do projeto:

```powershell
dotnet run --project .\WalletCopy.csproj
```

Para compilar sem restaurar:

```powershell
dotnet build .\WalletCopy.csproj --no-restore
```
