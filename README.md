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

### Atualizar

Quando sai uma versão nova, aparece "Nova versão" na barra lateral do app, com o que mudou. O app só avisa e
abre o download no navegador; ele nunca baixa `.exe` sozinho.
1. Baixe o `.exe` novo (pelo aviso ou pelos links acima).
2. Feche a janela do app.
3. Abra o arquivo baixado. Se houver uma versão mais antiga instalada em `%LOCALAPPDATA%\NfseApp\bin\` (a que o
   atalho da área de trabalho abre), ele pergunta se instala no lugar dela. Com "Sim", ele copia a antiga para
   `NfseApp.anterior.exe`, se copia por cima dela, confere o SHA-256 e abre a instalada. O atalho continua o mesmo,
   e as notas e configurações não mudam.

Sem versão instalada, o `.exe` baixado só abre, como sempre: os dados ficam em `%LOCALAPPDATA%\NfseApp\{CNPJ}\`, e
qualquer cópia do `.exe` vê os mesmos. Para voltar atrás, use o `NfseApp.anterior.exe` ou o link de uma versão
anterior em Releases. A instalação está em `Instalacao.cs`; o aviso, em `Atualizacao.cs`.

## Usar

1. Rode o `.exe` (duplo clique). Ele abre uma janela própria, um Edge em modo aplicativo sem barra de endereço.
   Fechar a janela encerra o app. Sem Edge, ele abre no navegador padrão.
2. Escolha o certificado e-CNPJ. Ele precisa estar instalado em *Certificados do usuário → Pessoal*.
3. Na versão de teste, escolha o ambiente no topo da tela. **Homologação** é teste, sem valor fiscal.
   **Produção** emite a nota de verdade e só deixa emitir depois de marcar a confirmação.

**Enviar a nota ao cliente:** o botão de e-mail (na lista e depois de emitir) abre uma mensagem nova no Outlook
para o e-mail do tomador, com assunto, texto e o PDF e o XML da nota anexados. O app nunca envia sozinho: você
confere e clica em Enviar. O remetente é a conta do seu Outlook. A assinatura não entra sozinha: no Outlook,
*Inserir > Assinatura*. O Sistema Nacional não manda e-mail ao tomador; ele só disponibiliza a nota no Ambiente
Nacional. A mensagem é um arquivo `.eml` marcado como não enviado (`X-Unsent: 1`), aberto com `OUTLOOK.EXE /eml`.
Sem o Outlook clássico, o `.eml` abre no programa de e-mail padrão (`Email.cs`).

**Regra do app: ele nunca chama o PowerShell nem roda scripts.** O antivírus corporativo trata isso como comportamento
de ataque. O `tools/build.sh` recusa compilar se aparecer "powershell" no código do app.

Cada pessoa usa o certificado instalado na própria máquina. O app não tem servidor central e nenhum certificado
sai do computador de ninguém. Como o `.exe` não é assinado, o Windows SmartScreen pode avisar na primeira execução
("Mais informações → Executar assim mesmo").

## De onde vêm os dados

Nada é cadastrado à mão. Tudo vem do Sistema Nacional da NFS-e pelo certificado:
- **Notas e cancelamentos:** baixados do ADN (Ambiente de Dados Nacional). O app atualiza sozinho ao entrar, e também pelo botão "Atualizar do ADN".
- **Tomadores:** saem das notas já emitidas em produção (nome e e-mail da nota mais recente). Para um cliente
  novo, emita a primeira nota pelo Emissor Nacional; depois ele aparece no app.
- **Nota nova:** copia a última nota válida do tomador e troca só o que você editou.

**Número da nota e número da DPS:** a DPS é a sua sequência (série 1: ..., 179, 180, 181). O número da NFS-e é dado
por quem gera a nota. Até a 179, as notas eram geradas pelo sistema da prefeitura, que usava o mesmo número da DPS
(`ambGer=1`, transcrita). As notas emitidas pelo app são geradas pelo Sistema Nacional (`ambGer=2`), que tem
numeração própria e começou em 1: a DPS 180 virou a NFS-e nº 1. A lista mostra os dois quando são diferentes, e a
busca encontra pelos dois.

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

**Produção e homologação estão em versões diferentes das regras** (11/2026). A homologação já roda as regras
novas (RTC) e exige os ajustes da tabela acima. A produção ainda **recusa** o `finNFSe` (E1235). Por isso o
formato depende do ambiente (`Leiaute` em `Dps.cs`):
- **Produção:** a mesma estrutura da nota anterior, que a própria produção aceitou, trocando só o indicador de
  tributos pelo % do Simples quando ele é informado.
- **Homologação:** com os três ajustes da tabela.

Se a Sefin recusar por um desses três códigos, o app ajusta e reenvia com o mesmo número. Recusa não cria nota,
então reenviar é seguro. O formato que passou fica salvo por ambiente (`leiaute.json`), e isso já cobre o dia em
que a produção adotar as regras novas. A bateria confere que a DPS de produção tem os mesmos campos, na mesma ordem,
da nota anterior, e confere o ajuste automático contra a Sefin real, em homologação.

Regras de segurança:
- Nota cancelada nunca é usada como base.
- O número da DPS é o maior número já autorizado na série + 1.
- **E0014** quer dizer que o número já virou uma nota. O app consulta qual nota foi (`GET /dps/{id}`) e mostra para
  você, em vez de pular de número sozinho. Pode ser uma tentativa anterior que deu certo, e pular de número geraria
  nota em dobro.
- Se a rede cair no envio, o app pergunta à Sefin se a DPS virou nota antes de deixar tentar de novo.

## Desenvolver

```bash
tools/build.sh            # gera as duas versões (~19 MB)
```

**Sem bateria automática por enquanto:** a antiga era um script PowerShell, e PowerShell é proibido na máquina de
desenvolvimento, porque o antivírus corporativo reage. Ela foi removida e ainda não foi reescrita. Até lá, teste à mão
antes de publicar, rodando o `.exe` direto (opções abaixo) e chamando a API dele com o `curl.exe` do Windows.
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
- `--versao`: escreve "NfseApp {versão} {arquivo da edição}" e sai.
- `--instalar`: instala este `.exe` no lugar da cópia instalada sem perguntar (é o que os testes usam) e sai com um
  código: 0 instalou, 2 sem instalação, 3 já atualizado, 4 a instalada está aberta, 5 a instalada é mais nova, 1 falha.

Como é WinExe, ele não mostra erro de inicialização. Para ver o erro, rode `./NfseApp.exe --sem-navegador > saida.txt 2>&1`.

### Testes

Não há bateria automática no momento (ver "Desenvolver"). A que existia rodava contra o `.exe` de verdade, numa pasta
de dados temporária (`NFSEAPP_DADOS`), e cobria login, leitura do ADN, DANFSe, proteções de produção, emissão em
homologação com XSD e assinatura, E0014, e-mail, aviso de versão e instalação local. A ideia é reescrevê-la como um
`.exe` de testes em C#, sem scripts. Os XSD oficiais continuam em `tests/xsd/` para isso.

### Publicar uma versão

Suba `<Version>` no `NfseApp.csproj`, rode `tools/build.sh`, teste à mão e:

```bash
gh release create vX.Y.Z dist/reduzido/producao/NfseApp.exe dist/reduzido/teste/NfseApp-Teste.exe --title "vX.Y.Z" --notes "..."
```

### PDF da nota (DANFSe)

O botão **PDF** abre a nota no portal nacional (Emissor Nacional), no navegador padrão:
`https://www.nfse.gov.br/EmissorNacional/Notas/Visualizar/Index/{chave}` (em homologação, `www.producaorestrita...`).
O PDF oficial fica lá. O app não baixa sozinho porque o portal pede um captcha (hCaptcha) para baixar o PDF ou o XML,
e a API nacional de DANFSe está suspensa desde 03/08/2026
([NT 008/2026 SE/CGNFS-e, v1.02](https://www.gov.br/nfse/pt-br/biblioteca/documentacao-tecnica/rtc/nt-008-se-cgnfse-danfse-20260714-v1-02.pdf)).
Se o navegador não estiver logado no portal, ele cai no login. O acesso por certificado leva para a página inicial,
que lista as últimas notas.

Na setinha ao lado do botão fica o **PDF gerado pelo app**: na hora, sem rede, a partir do XML guardado
(`Danfse.cs`). Ele segue o modelo do Anexo I da NT 008 e, onde a NT deixa margem, copia o PDF do portal:
- medidas em pontos tiradas do PDF do portal. Sobrepondo os dois PDFs da mesma nota, os textos ficam a menos de
  0,6 pt (0,2 mm) e as fontes e os tamanhos são os mesmos;
- textos e formatos do portal: códigos no cabeçalho ("Ambiente Gerador: 2"), telefone e código IBGE formatados,
  " / -" para código municipal e país ausentes, "0 - PIS/COFINS/CSLL Não Retidos", exclusões da base do IBS/CBS
  calculadas, R$ 0,00 no IBS/CBS quando a nota não tem esse grupo;
- telefone e e-mail do prestador só da DPS (nome e endereço vêm do cadastro quando a DPS não traz);
- A4, uma página;
- só o que está no XML; campo sem informação sai como "-" (nota 12);
- QR Code idêntico ao do portal, módulo por módulo: mesmo endereço (`ConsultaPublica?tpc=1&chave=`), modo byte
  (`forceUtf8`), nível M, 41 módulos, imagem PNG de 10 px por módulo como a do portal (em SVG o Edge deformava os
  módulos ao alinhar cada um à grade de pixels);
- "NFS-e SEM VALIDADE JURÍDICA" em homologação, e marca d'água CANCELADA ou SUBSTITUÍDA;
- nome do município pela tabela do IBGE (`assets/municipios.csv`, gerada por `tools/gerar_municipios.py`);
- logo da NFS-e igual ao do PDF do portal (`assets/logo-nfse.png`).

O PDF é impresso pelo próprio Edge em modo headless. Sem Edge na máquina, o app devolve o HTML para imprimir pelo navegador.
O Edge alinha cada linha de texto à grade de pixels (0,75 pt). Por isso as linhas divisórias são desenhadas fora do
layout e há pequenos ajustes de posição por tipo de texto no CSS.
Na linha de "Totais aproximados dos Tributos", o portal só mostra valores ou percentuais por esfera. O % do Simples
(`pTotTribSN`) e o `indTotTrib` saem como "-", lá e aqui, embora estejam no XML.

Ainda não faz: e-CPF, cancelamento e nota sem uma nota anterior para copiar.
