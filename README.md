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
cd src/NfseApp
OPC="-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none"
dotnet publish $OPC -o ../../dist/teste                         # NfseApp-Teste.exe (produção + homologação)
dotnet publish $OPC -p:SoProducao=true -o ../../dist/producao   # NfseApp.exe (só produção)
```

As diferenças entre as duas versões estão todas em `Edicao.cs`. O ícone é gerado por `tools/gerar_icone.py`.

O app precisa rodar no Windows, porque a chave do certificado não é exportável. No WSL, compile e rode o `.exe`
pelo interop. A opção `--sem-navegador` sobe sem abrir janela, e um número muda a porta (ex.: `NfseApp.exe 5200`).
Como é WinExe, ele não mostra erro de inicialização. Para ver o erro, rode
`./NfseApp.exe --sem-navegador > saida.txt 2>&1`.

### Publicar uma versão

Suba `<Version>` no `NfseApp.csproj`, gere as duas versões e:

```bash
gh release create vX.Y.Z dist/producao/NfseApp.exe dist/teste/NfseApp-Teste.exe --title "vX.Y.Z" --notes "..."
```

Ainda não faz: e-CPF, cancelamento, nota sem uma nota anterior para copiar, e o PDF na homologação (a API
responde 501 lá). Em produção o PDF vem do ADN (`/danfse/{chave}`), que estava fora do ar (503) no dia do teste.
