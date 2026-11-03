# NFS-e

App local para emitir NFS-e pelo Sistema Nacional (Sefin/ADN) com o certificado A1 instalado no Windows.
Lista as notas emitidas e recebidas, os tomadores, e emite uma nota nova copiando a última nota válida do tomador.

## Baixar

| Versão | Para quem | Download |
|---|---|---|
| **NfseApp.exe** | Uso do dia a dia. Só produção. | [baixar](https://github.com/marcelopedroso/nfse-app/releases/latest/download/NfseApp.exe) |
| **NfseApp-Teste.exe** | Quem quer testar antes. Produção e homologação. | [baixar](https://github.com/marcelopedroso/nfse-app/releases/latest/download/NfseApp-Teste.exe) |

Os links sempre baixam a versão mais nova. As duas versões podem ficar abertas ao mesmo tempo: usam portas
(5199 e 5198) e janelas diferentes.

## Usar

1. Rode o `.exe` (duplo clique). Ele abre uma janela própria, um Edge em modo aplicativo sem barra de endereço.
   Fechar a janela encerra o app. Sem Edge, ele abre no navegador padrão.
2. Escolha o certificado e-CNPJ. Ele precisa estar instalado em *Certificados do usuário → Pessoal*.
3. Na versão de teste, escolha o ambiente no topo da tela. **Homologação** é teste, sem valor fiscal.
   **Produção** emite a nota de verdade e só deixa emitir depois de marcar a confirmação.

Cada pessoa usa o certificado instalado na própria máquina. O app não tem servidor central e nenhum certificado
sai do computador de ninguém. Como o `.exe` não é assinado, o Windows SmartScreen pode avisar na primeira execução
("Mais informações → Executar assim mesmo").

## De onde vêm os dados

Nada é cadastrado à mão. Tudo vem do Sistema Nacional da NFS-e pelo certificado:
- **Notas e cancelamentos:** baixados do ADN (Ambiente de Dados Nacional). O app atualiza sozinho ao entrar, e também pelo botão "Atualizar do ADN".
- **Tomadores:** saem das notas já emitidas em produção (nome e e-mail da nota mais recente). Para um cliente
  novo, emita a primeira nota pelo Emissor Nacional; depois ele aparece no app.
- **Nota nova:** copia a última nota válida do tomador e troca só o que você editou.

Cópia local do que já foi baixado ou emitido: `%LOCALAPPDATA%\NfseApp\{CNPJ}\{prod|hom}\`
- `nfse/`: XML das notas baixadas do ADN e das emitidas pelo app
- `eventos/`: cancelamentos
- `dps/`: cada DPS assinada que o app enviou
- `estado.json`: último NSU baixado

A fonte da verdade é o ADN. Se apagar a pasta, o app baixa tudo de novo.

## Como a nota nova é montada

O app copia a DPS de dentro da última NFS-e autorizada e troca alguns campos: ambiente, data, competência, número,
valor, desconto e descrição. A base de cálculo de PIS e COFINS é recalculada. A DPS também é ajustada para o
leiaute do SefinNacional 1.6.0 (em uso em 11/2026), que rejeita uma cópia literal:

| Erro | Ajuste |
|---|---|
| E1235 | inclui `finNFSe=0` depois de `tpEmit` |
| E0625 | ME/EPP sem retenção de ISS: remove `pAliq` |
| E0712 | Simples: `totTrib` vira `pTotTribSN` (% informado na tela e lembrado para as próximas notas) |

Regras de segurança:
- Nota cancelada nunca é usada como base.
- O número da DPS é o maior número já autorizado na série + 1.
- **E0014** quer dizer que o número já virou uma nota. O app consulta qual nota foi (`GET /dps/{id}`) e mostra para
  você, em vez de pular de número sozinho. Pode ser uma tentativa anterior que deu certo, e pular de número geraria
  nota em dobro.
- Se a rede cair no envio, o app pergunta à Sefin se a DPS virou nota antes de deixar tentar de novo.

## Desenvolver

```bash
tools/build.sh            # gera as duas versões (~19 MB) e roda a bateria de testes em cada .exe
```

`tools/build.sh` só termina com sucesso se todos os testes passarem. **Não publique um `.exe` que não passou por ele.**
Modos: `reduzido` (padrão, ~19 MB), `comprimido` (~45 MB) e `normal` (~95 MB).

**Por que o `.exe` é grande:** o código do app tem menos de 300 KB. O resto é o .NET, que vai dentro do `.exe`
para ninguém precisar instalar nada. O modo `reduzido` remove do .NET o que o app não usa (trimming parcial) e
comprime o resto. O trimming pode remover coisas usadas por reflexão: foi assim que a assinatura do XML quebrou
na primeira tentativa. Por isso as bibliotecas de criptografia são preservadas no `NfseApp.csproj`, e a bateria
testa a assinatura nas duas versões.

As diferenças entre as duas versões estão todas em `Edicao.cs`. O ícone é gerado por `tools/gerar_icone.py`.

O app precisa rodar no Windows, porque a chave do certificado não é exportável. No WSL, compile e rode o `.exe`
pelo interop. Opções de linha de comando:
- `--sem-navegador`: sobe sem abrir janela.
- um número: muda a porta preferida (ex.: `NfseApp.exe 5200`). Se a porta estiver ocupada por outro programa,
  o app sobe numa livre.
- `--autoteste saida.xml`: monta e assina uma DPS sem rede e confere a assinatura.

Como é WinExe, ele não mostra erro de inicialização. Para ver o erro, rode `./NfseApp.exe --sem-navegador > saida.txt 2>&1`.

### Testes (`tests/testar.ps1`)

A bateria roda contra o `.exe` de verdade, no Windows, com o certificado instalado, numa pasta de dados temporária
(`NFSEAPP_DADOS`) e em portas próprias (5290/5291). Ela cobre:
- inicialização, segurança (403/401) e login;
- leitura do ADN, notas, tomadores e XML;
- DANFSe: conteúdo, QR Code, marca d'água, aviso de homologação, PDF de 1 página A4;
- proteções de produção;
- validações dos campos;
- emissão real em **homologação**, com o XML conferido campo a campo, a DPS validada no XSD oficial e a assinatura verificada;
- E0014 sem nota em dobro;
- autoteste de assinatura nas duas versões;
- instância única e porta ocupada.

Ela **nunca** manda confirmação de produção: os testes de proteção usam uma nota-base inexistente, então mesmo uma
proteção quebrada pararia antes da Sefin.

### Publicar uma versão

Suba `<Version>` no `NfseApp.csproj`, rode `tools/build.sh` até dar `TUDO OK` e:

```bash
gh release create vX.Y.Z dist/reduzido/producao/NfseApp.exe dist/reduzido/teste/NfseApp-Teste.exe --title "vX.Y.Z" --notes "..."
```

### PDF da nota (DANFSe)

A Receita suspendeu a API nacional de DANFSe em 03/08/2026
([NT 008/2026 SE/CGNFS-e, v1.02](https://www.gov.br/nfse/pt-br/biblioteca/documentacao-tecnica/rtc/nt-008-se-cgnfse-danfse-20260714-v1-02.pdf)).
Desde então, o sistema emissor é quem gera o DANFSe. O app gera localmente, sem rede, a partir do XML guardado
(`Danfse.cs`), seguindo o modelo do Anexo I da nota:
- A4, uma página, com as medidas, fontes e sombreamentos do item 2.4;
- só o que está no XML; campo sem informação sai como "-" (nota 12);
- QR Code apontando para a consulta pública da chave, gerado com a biblioteca QRCoder;
- "NFS-e SEM VALIDADE JURÍDICA" em homologação, e marca d'água CANCELADA ou SUBSTITUÍDA;
- nome do município pela tabela do IBGE (`assets/municipios.csv`, gerada por `tools/gerar_municipios.py`);
- logo oficial da NFS-e (`assets/logo-nfse.png`).

O PDF é impresso pelo próprio Edge em modo headless. Sem Edge na máquina, o app devolve o HTML para imprimir pelo navegador.
Para notas do Simples (`pTotTribSN`) ou sem total de tributos (`indTotTrib`), que a nota técnica não prevê na
linha de "Totais Aproximados", o app mostra o que o XML traz.

Ainda não faz: e-CPF, cancelamento e nota sem uma nota anterior para copiar.
