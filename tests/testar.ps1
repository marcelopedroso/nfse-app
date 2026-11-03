<#
Testes automatizados do NfseApp, rodando contra o .exe de verdade (é o binário publicado que pode quebrar,
por exemplo com trimming), no Windows, com o certificado e-CNPJ instalado.

    powershell -NoProfile -ExecutionPolicy Bypass -File tests\testar.ps1 -Exe dist\teste\NfseApp-Teste.exe

Regras de segurança:
- Os dados vão para uma pasta temporária (variável NFSEAPP_DADOS); a pasta real do usuário não é tocada.
- Portas próprias (5290/5291), para não encostar numa instância do app que esteja aberta.
- NUNCA manda confirmação de produção. Os testes de proteção usam uma nota-base inexistente:
  se a proteção quebrasse, o pedido morreria na validação da base, antes de chegar à Sefin.
- Só emite em homologação (sem valor fiscal), e só na edição de teste: 2 notas por rodada.

Sai com código 0 se tudo passou, 1 se algo falhou.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$Porta = 5290,
    [string]$Thumbprint = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Security

$script:passou = 0
$script:falhou = 0
$script:avisos = 0
$processos = New-Object System.Collections.ArrayList

function Ok([string]$nome, [bool]$condicao, [string]$detalhe = '') {
    if ($condicao) { $script:passou++; Write-Host "  ok     $nome" }
    else { $script:falhou++; Write-Host "  FALHOU $nome  $detalhe" }
}
function Aviso([string]$texto) { $script:avisos++; Write-Host "  aviso  $texto" }
function Secao([string]$nome) { Write-Host ''; Write-Host "== $nome" }

# ---------------------------------------------------------------- HTTP
function NovoCliente {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.UseCookies = $true
    $c = New-Object System.Net.Http.HttpClient($h)
    $c.Timeout = [TimeSpan]::FromSeconds(240)
    return $c
}

function Chamar($cliente, [string]$metodo, [string]$url, $corpo = $null, [switch]$SemCabecalho, [string]$HostHeader = '') {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($metodo)), $url)
    if (-not $SemCabecalho) { $req.Headers.Add('X-NfseApp', '1') }
    if ($HostHeader) { $req.Headers.Host = $HostHeader }
    if ($null -ne $corpo) {
        $json = $corpo | ConvertTo-Json -Depth 6 -Compress
        $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    }
    $resp = $cliente.SendAsync($req).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $tipo = "$($resp.Content.Headers.ContentType)"
    $texto = ''
    if ($tipo -match 'json|text|xml|html') { $texto = [Text.Encoding]::UTF8.GetString($bytes) }
    $obj = $null
    if ($tipo -match 'json' -and $texto) { $obj = $texto | ConvertFrom-Json }
    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Tipo = $tipo; Texto = $texto; Json = $obj; Bytes = $bytes }
}

# ---------------------------------------------------------------- processo
function Iniciar([string]$exe, [int]$porta, [string]$saida) {
    $p = Start-Process -FilePath $exe -ArgumentList @("$porta", '--sem-navegador') -PassThru `
        -RedirectStandardOutput $saida -RedirectStandardError "$saida.err"
    [void]$processos.Add($p)
    return $p
}

function EsperarUrl([string]$saida, [int]$segundos = 60) {
    $limite = (Get-Date).AddSeconds($segundos)
    while ((Get-Date) -lt $limite) {
        if (Test-Path $saida) {
            $m = [regex]::Match((Get-Content $saida -Raw -ErrorAction SilentlyContinue) + '', 'rodando em (http://\S+)')
            if ($m.Success) { return $m.Groups[1].Value.TrimEnd('/') }
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Encerrar($p) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit(10000) | Out-Null } }

# ---------------------------------------------------------------- XML
$NS = 'http://www.sped.fazenda.gov.br/nfse'
function Tomador([xml]$doc, [string]$nome) {
    $t = $doc.GetElementsByTagName('toma', $NS)
    if ($t.Count -eq 0) { return $null }
    $n = $t[0].GetElementsByTagName($nome, $NS)
    if ($n.Count -gt 0) { return $n[0].InnerText } else { return $null }
}
function Valor([xml]$doc, [string]$nome) {
    $n = $doc.GetElementsByTagName($nome, $NS)
    if ($n.Count -gt 0) { return $n[0].InnerText } else { return $null }
}

# Nomes dos campos do infDPS, em ordem (a "forma" da DPS, sem os valores).
function Estrutura([xml]$doc) {
    $inf = $doc.GetElementsByTagName('infDPS', $NS)[0]
    $nomes = New-Object System.Collections.ArrayList
    foreach ($n in $inf.SelectNodes('.//*')) { if ($n.NamespaceURI -eq $NS) { [void]$nomes.Add($n.LocalName) } }
    return , $nomes
}

function ValidarXsd([string]$arquivo) {
    $pastaXsd = Join-Path $PSScriptRoot 'xsd'
    $config = New-Object System.Xml.XmlReaderSettings
    $config.ValidationType = [System.Xml.ValidationType]::Schema
    $config.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $config.Schemas.XmlResolver = New-Object System.Xml.XmlUrlResolver
    [void]$config.Schemas.Add($NS, (Join-Path $pastaXsd 'DPS_v1.01.xsd'))
    $erros = New-Object System.Collections.ArrayList
    $config.add_ValidationEventHandler({ param($s, $e) [void]$erros.Add($e.Message) })
    $leitor = [System.Xml.XmlReader]::Create($arquivo, $config)
    try { while ($leitor.Read()) { } } finally { $leitor.Close() }
    return , $erros
}

function AssinaturaValida([string]$arquivo) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.Load($arquivo)
    $sig = $doc.GetElementsByTagName('Signature', 'http://www.w3.org/2000/09/xmldsig#')
    if ($sig.Count -ne 1) { return $false }
    $signed = New-Object System.Security.Cryptography.Xml.SignedXml($doc)
    $signed.LoadXml([System.Xml.XmlElement]$sig[0])
    return $signed.CheckSignature()
}

# =====================================================================================
$exeOrigem = (Resolve-Path $Exe).Path
$tmp = Join-Path $env:TEMP ("nfseapp-teste-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path "$tmp\dados", "$tmp\bin" | Out-Null
$exe = Join-Path "$tmp\bin" (Split-Path $exeOrigem -Leaf)
Copy-Item $exeOrigem $exe
$env:NFSEAPP_DADOS = "$tmp\dados"

Write-Host "Testando $(Split-Path $exeOrigem -Leaf) ($([math]::Round((Get-Item $exeOrigem).Length / 1MB, 1)) MB)"
Write-Host "Dados temporários: $tmp\dados"

try {
    # ------------------------------------------------------------------ inicialização
    Secao 'Inicialização'
    $inicio = Get-Date
    $proc = Iniciar $exe $Porta "$tmp\saida1.txt"
    $base = EsperarUrl "$tmp\saida1.txt"
    Ok 'sobe e informa o endereço' ($null -ne $base) ("saída: " + (Get-Content "$tmp\saida1.txt*" -Raw -ErrorAction SilentlyContinue))
    if (-not $base) { throw 'O app não subiu; não dá para seguir.' }
    Ok "usa a porta pedida ($Porta)" ($base -eq "http://127.0.0.1:$Porta") $base
    Write-Host ("         subiu em {0:N1} s" -f ((Get-Date) - $inicio).TotalSeconds)

    $anon = NovoCliente
    $r = Chamar $anon GET "$base/api/info"
    Ok '/api/info responde' ($r.Status -eq 200 -and $r.Json.app -eq 'NfseApp')
    $info = $r.Json
    $temHom = [bool]$info.temHomologacao
    Write-Host "         edição: $($info.nome) v$($info.versao) (homologação: $temHom)"
    Ok 'pasta de dados é a temporária (dados reais intocados)' ($info.pastaDados -eq "$tmp\dados") $info.pastaDados
    if ($info.pastaDados -ne "$tmp\dados") { throw 'Pasta de dados não isolada; parando para não mexer nos dados reais.' }

    $r = Chamar $anon GET "$base/"
    Ok 'página carrega' ($r.Status -eq 200 -and $r.Texto -match 'id="app"' -and $r.Texto -match 'icone\.png')
    $r = Chamar $anon GET "$base/icone.png"
    Ok 'ícone é PNG' ($r.Status -eq 200 -and $r.Bytes.Length -gt 1000 -and $r.Bytes[1] -eq 0x50 -and $r.Bytes[2] -eq 0x4E -and $r.Bytes[3] -eq 0x47)

    # ------------------------------------------------------------------ segurança
    Secao 'Segurança'
    Ok 'POST sem cabeçalho do app é barrado (403)' ((Chamar $anon POST "$base/api/logout" @{} -SemCabecalho).Status -eq 403)
    Ok 'Host de outro site é barrado (403)' ((Chamar $anon GET "$base/api/info" -HostHeader "evil.example:$Porta").Status -eq 403)
    Ok 'sem login não lista notas (401)' ((Chamar $anon GET "$base/api/notas?amb=prod").Status -eq 401)

    # ------------------------------------------------------------------ login
    Secao 'Login com certificado'
    $r = Chamar $anon GET "$base/api/certificados"
    $certs = @($r.Json | Where-Object { $_.tipoDocumento -eq 'CNPJ' })
    Ok 'lista certificados e-CNPJ' ($r.Status -eq 200 -and $certs.Count -ge 1)
    $cert = $certs[0]
    if ($Thumbprint) { $cert = $certs | Where-Object { $_.thumbprint -eq $Thumbprint } | Select-Object -First 1 }
    $cli = NovoCliente
    $r = Chamar $cli POST "$base/api/login" @{ thumbprint = $cert.thumbprint }
    Ok 'login com o certificado' ($r.Status -eq 200 -and $r.Json.documento -eq $cert.documento) $r.Texto
    $cnpj = $cert.documento
    Ok 'sessão ativa' ((Chamar $cli GET "$base/api/sessao").Status -eq 200)

    # ------------------------------------------------------------------ produção (só leitura)
    Secao 'Produção: leitura do ADN'
    $r = Chamar $cli POST "$base/api/sincronizar?amb=prod" @{}
    Ok 'baixa as notas do ADN' ($r.Status -eq 200 -and $r.Json.novos -gt 0) $r.Texto
    $r = Chamar $cli POST "$base/api/sincronizar?amb=prod" @{}
    Ok 'segunda atualização é incremental (0 novos)' ($r.Status -eq 200 -and $r.Json.novos -eq 0) $r.Texto

    $r = Chamar $cli GET "$base/api/notas?amb=prod"
    $emitidas = @($r.Json.emitidas)
    Ok 'lista notas emitidas' ($r.Status -eq 200 -and $emitidas.Count -gt 0)
    Ok 'todas as emitidas são deste CNPJ' (@($emitidas | Where-Object { $_.prestDoc -ne $cnpj }).Count -eq 0)
    $canceladas = @($emitidas | Where-Object { $_.cancelada })
    Ok 'canceladas trazem o motivo' (@($canceladas | Where-Object { -not $_.motivoCancelamento }).Count -eq 0)
    $validas = @($emitidas | Where-Object { -not $_.cancelada } | Sort-Object dhProc -Descending)

    $r = Chamar $cli GET "$base/api/tomadores"
    $tomadores = @($r.Json)
    Ok 'lista tomadores' ($r.Status -eq 200 -and $tomadores.Count -gt 0)
    $numerosValidos = @($validas | ForEach-Object { $_.numero })
    Ok 'tomadores vêm só de notas válidas' (@($tomadores | Where-Object { $numerosValidos -notcontains $_.ultimoNumero }).Count -eq 0)

    $r = Chamar $cli GET "$base/api/notas/$($emitidas[0].chave)/xml?amb=prod"
    Ok 'baixa o XML da nota' ($r.Status -eq 200 -and $r.Texto -match "NFS$($emitidas[0].chave)")

    $r = Chamar $cli GET "$base/api/nova?amb=prod"
    $nova = $r.Json
    Ok 'prepara nova nota (só leitura)' ($r.Status -eq 200)
    Ok 'nota-base é a última válida' ($nova.base.chave -eq $validas[0].chave) "$($nova.base.numero) vs $($validas[0].numero)"
    $maiorDps = ($emitidas | Where-Object { $_.serie -eq $nova.serie } | Measure-Object -Property nDps -Maximum).Maximum
    Ok 'próximo número de DPS = maior usado + 1' ($nova.proximoNDps -eq $maiorDps + 1) "$($nova.proximoNDps) vs $maiorDps"
    Ok 'traz a tributação da nota-base' ($null -ne $nova.tributacao -and $nova.tributacao.regime)


    # ------------------------------------------------------------------ DANFSe
    Secao 'DANFSe gerado pelo app (NT 008/2026; a API nacional foi suspensa em 03/08/2026)'
    $nv = $validas[0]
    $r = Chamar $cli GET "$base/api/notas/$($nv.chave)/danfse?amb=prod"
    $html = [Net.WebUtility]::HtmlDecode($r.Texto)
    Ok 'DANFSe (HTML) é gerado' ($r.Status -eq 200 -and $html -match 'DANFSe v2\.0' -and $html -match 'Documento Auxiliar da NFS-e')
    Ok 'traz a chave, o número e o tomador' ($html.Contains($nv.chave) -and $html.Contains(">$($nv.numero)<") -and $html.Contains($nv.tomaNome))
    Ok 'QR Code aponta para a consulta pública da chave' ($html -match '<svg' -and $html.Contains("data-qr=`"https://www.nfse.gov.br/ConsultaPublica/?tpc=1&chave=$($nv.chave)`""))
    Ok 'linha obrigatória de totais aproximados de tributos' ($html.Contains('Totais Aproximados dos Tributos cfe. Lei nº 12.741/2012'))
    Ok 'nota de produção não leva aviso de homologação' (-not $html.Contains('SEM VALIDADE'))
    Ok 'nota válida sem marca d''água' (-not $html.Contains('class="marca"'))
    if ($canceladas.Count -gt 0) {
        $hc = [Net.WebUtility]::HtmlDecode((Chamar $cli GET "$base/api/notas/$($canceladas[0].chave)/danfse?amb=prod").Texto)
        Ok 'nota cancelada sai com marca d''água CANCELADA' ($hc.Contains('class="marca">CANCELADA<'))
    }
    $r = Chamar $cli GET "$base/api/notas/$($nv.chave)/pdf?amb=prod"
    $pdfTexto = [Text.Encoding]::ASCII.GetString($r.Bytes)
    Ok 'PDF gerado pelo Edge' ($r.Status -eq 200 -and $r.Tipo -match 'pdf' -and $pdfTexto.StartsWith('%PDF-')) "HTTP $($r.Status) $($r.Tipo) $($r.Texto)"
    $paginas = ([regex]::Matches($pdfTexto, '/Count (\d+)') | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Maximum).Maximum
    Ok 'PDF tem uma página só (NT 008, item 2.2)' ($paginas -eq 1) "páginas: $paginas"
    Ok 'PDF em A4 retrato' ($pdfTexto -match '/MediaBox \[0 0 59[45]\.\d+ 84[12]\.\d+\]')

    # ------------------------------------------------------------------ assinatura sem rede
    Secao 'Assinatura sem rede (autoteste do .exe, nas duas edições)'
    $pa = Start-Process -FilePath $exe -ArgumentList @('--autoteste', "`"$tmp\autoteste.xml`"", "`"$tmp\autoteste-prod.xml`"") -PassThru -Wait `
        -RedirectStandardOutput "$tmp\autoteste.txt" -RedirectStandardError "$tmp\autoteste.err"
    $saidaAuto = (Get-Content "$tmp\autoteste.txt" -Raw -ErrorAction SilentlyContinue) + ''
    Ok 'o .exe monta e assina uma DPS e confere a assinatura' ($pa.ExitCode -eq 0 -and $saidaAuto -match 'AUTOTESTE OK') $saidaAuto
    if (Test-Path "$tmp\autoteste.xml") {
        $errosXsd = ValidarXsd "$tmp\autoteste.xml"
        Ok 'DPS do autoteste passa no XSD oficial' ($errosXsd.Count -eq 0) ($errosXsd -join ' | ')
        Ok 'assinatura do autoteste confere (verificação independente)' (AssinaturaValida "$tmp\autoteste.xml")
    }

    # Produção não tem schema público atual; a régua é a nota anterior, que a própria produção aceitou.
    Secao 'DPS de produção (montada, nunca enviada) tem a forma que a produção já aceitou'
    if (Test-Path "$tmp\autoteste-prod.xml") {
        [xml]$dpsProd = Get-Content "$tmp\autoteste-prod.xml" -Raw -Encoding UTF8
        [xml]$notaBase = (Chamar $cli GET "$base/api/notas/$($validas[0].chave)/xml?amb=prod").Texto
        $formaBase = Estrutura $notaBase
        $formaProd = foreach ($nome in (Estrutura $dpsProd)) { if ($nome -eq 'pTotTribSN' -and $formaBase -contains 'indTotTrib') { 'indTotTrib' } else { $nome } }
        Ok 'sem finNFSe (a produção recusa: E1235)' ($formaProd -notcontains 'finNFSe' -or $formaBase -contains 'finNFSe')
        Ok 'mesmos campos, na mesma ordem, da nota anterior' (($formaProd -join ',') -eq ($formaBase -join ',')) ("prod: " + ($formaProd -join ',') + " | base: " + ($formaBase -join ','))
        Ok 'tpAmb de produção e % do Simples informado' ((Valor $dpsProd 'tpAmb') -eq '1' -and (Valor $dpsProd 'pTotTribSN') -eq '10.94')
        Ok 'assinatura da DPS de produção confere' (AssinaturaValida "$tmp\autoteste-prod.xml")
    }
    else { Ok 'autoteste gerou a DPS de produção' $false }

    # ------------------------------------------------------------------ proteções de produção
    Secao 'Proteções de produção (nada chega à Sefin)'
    $pedidoBarrado = @{ amb = 'prod'; baseChave = 'INEXISTENTE'; dCompet = (Get-Date -Format 'yyyy-MM-dd'); vServ = 1; vDescIncond = 0; descricao = 'teste'; pTotTribSN = 10.94; confirmoProducao = $false }
    $r = Chamar $cli POST "$base/api/emitir" $pedidoBarrado
    Ok 'produção sem confirmação é barrada' ($r.Status -eq 400 -and $r.Json.erro -match 'confirma') $r.Texto
    if (-not $temHom) {
        $pedidoBarrado.amb = 'hom'
        $r = Chamar $cli POST "$base/api/emitir" $pedidoBarrado
        Ok 'edição do dia a dia trata homologação como produção' ($r.Status -eq 400 -and $r.Json.erro -match 'confirma') $r.Texto
        $r = Chamar $cli GET "$base/api/nova?amb=hom"
        Ok 'nova nota na edição do dia a dia é sempre Produção' ($r.Json.ambiente -eq 'Produção') $r.Json.ambiente
    }

    # ------------------------------------------------------------------ homologação
    if ($temHom) {
        Secao 'Validações (homologação)'
        $hoje = Get-Date -Format 'yyyy-MM-dd'
        $valido = @{ amb = 'hom'; baseChave = $nova.base.chave; dCompet = $hoje; vServ = 1234.56; vDescIncond = 100.00
            descricao = "Teste automatizado $(Get-Date -Format 'dd/MM HH:mm:ss')"; pTotTribSN = 10.94; confirmoProducao = $false
            emailTomador = 'teste.nfseapp@example.com'; foneTomador = '42999990000' }
        function Invalido($mudanca, [string]$espera, [string]$nome) {
            $p = $valido.Clone(); foreach ($k in $mudanca.Keys) { $p[$k] = $mudanca[$k] }
            $r = Chamar $cli POST "$base/api/emitir" $p
            Ok $nome ($r.Status -eq 400 -and $r.Json.erro -match $espera) $r.Texto
        }
        Invalido @{ vServ = 0 } 'maior que zero' 'valor zero é recusado'
        Invalido @{ vDescIncond = 1234.56 } 'Desconto' 'desconto >= valor é recusado'
        Invalido @{ dCompet = (Get-Date).AddDays(3).ToString('yyyy-MM-dd') } 'futura' 'competência futura é recusada'
        Invalido @{ descricao = '  ' } 'Descri' 'descrição vazia é recusada'
        Invalido @{ emailTomador = 'sem-arroba' } 'E-mail' 'e-mail inválido é recusado'
        Invalido @{ foneTomador = '123' } 'Telefone' 'telefone curto é recusado'

        Secao 'Emissão em homologação'
        $r = Chamar $cli POST "$base/api/sincronizar?amb=hom" @{}
        Ok 'baixa as notas de homologação' ($r.Status -eq 200) $r.Texto
        $r = Chamar $cli GET "$base/api/nova?amb=hom"
        Ok 'nova nota em Homologação' ($r.Json.ambiente -eq 'Homologação') $r.Json.ambiente
        Ok 'e-mail real do cliente não vem sugerido em homologação' ($r.Json.sugestao.emailTomador -eq '') $r.Json.sugestao.emailTomador
        $emailReal = $r.Json.tributacao.emailTomador

        # Se o ADN ainda não distribuiu uma nota de teste anterior, o número sugerido já está usado (E0014).
        # O app mostra a nota existente e o usuário tenta de novo; o teste faz o mesmo.
        for ($tentativa = 1; $tentativa -le 5; $tentativa++) {
            $r = Chamar $cli POST "$base/api/emitir" $valido
            $e1 = $r.Json
            if ($e1.ok -or -not $e1.jaExistia) { break }
            Write-Host "         DPS $($e1.nDps) já era a nota nº $($e1.jaExistia.numero) (ADN atrasado); tentando o número seguinte"
        }
        Ok 'emite e a Sefin autoriza' ($r.Status -eq 200 -and $e1.ok) ($r.Texto)
        if (-not $e1.ok) { throw 'Emissão em homologação falhou; os testes seguintes dependem dela.' }
        Write-Host "         NFS-e nº $($e1.numero) (DPS $($e1.nDps))"

        $pastaHom = "$tmp\dados\$cnpj\hom"
        $r = Chamar $cli GET "$base/api/notas/$($e1.chave)/xml?amb=hom"
        [xml]$nf = $r.Texto
        Ok 'XML: ambiente de homologação (tpAmb 2)' ((Valor $nf 'tpAmb') -eq '2')
        Ok 'XML: finNFSe 0' ((Valor $nf 'finNFSe') -eq '0')
        Ok 'XML: valor e desconto editados' ((Valor $nf 'vServ') -eq '1234.56' -and (Valor $nf 'vDescIncond') -eq '100.00')
        Ok 'XML: PIS/COFINS recalculados (4,08 / 18,83)' ((Valor $nf 'vBCPisCofins') -eq '1134.56' -and (Valor $nf 'vPis') -eq '4.08' -and (Valor $nf 'vCofins') -eq '18.83') "$(Valor $nf 'vPis') / $(Valor $nf 'vCofins')"
        Ok 'XML: % do Simples' ((Valor $nf 'pTotTribSN') -eq '10.94')
        Ok 'XML: sem alíquota de ISS (E0625)' ($null -eq (Valor $nf 'pAliq'))
        Ok 'XML: e-mail e telefone do tomador editados' ((Tomador $nf 'email') -eq 'teste.nfseapp@example.com' -and (Tomador $nf 'fone') -eq '42999990000') "$(Tomador $nf 'email') / $(Tomador $nf 'fone')"
        Ok 'XML: descrição e competência' ((Valor $nf 'xDescServ') -eq $valido.descricao -and (Valor $nf 'dCompet') -eq $hoje)
        Ok 'XML: valor líquido' ((Valor $nf 'vLiq') -eq '1134.56') (Valor $nf 'vLiq')

        $hh = [Net.WebUtility]::HtmlDecode((Chamar $cli GET "$base/api/notas/$($e1.chave)/danfse?amb=hom").Texto)
        Ok 'DANFSe de homologação: "NFS-e SEM VALIDADE JURÍDICA"' ($hh.Contains('NFS-e SEM VALIDADE JURÍDICA') -and $hh.Contains('Tipo de Ambiente: Homologação'))
        Ok 'DANFSe mostra o e-mail editado e o valor' ($hh.Contains('teste.nfseapp@example.com') -and $hh.Contains('R$ 1.234,56'))

        $dps = Get-ChildItem "$pastaHom\dps" -Filter "*$('{0:D15}' -f [int64]$e1.nDps).xml" | Select-Object -First 1
        Ok 'DPS assinada foi guardada' ($null -ne $dps)
        if ($dps) {
            $errosXsd = ValidarXsd $dps.FullName
            Ok 'DPS assinada passa no XSD oficial' ($errosXsd.Count -eq 0) ($errosXsd -join ' | ')
            Ok 'assinatura da DPS confere' (AssinaturaValida $dps.FullName)
        }

        Secao 'Número repetido (E0014) sem nota em dobro'
        # Simula "a resposta se perdeu": apaga a nota do cache e impede a sincronização de trazê-la de volta.
        Remove-Item "$pastaHom\nfse\$($e1.chave).xml"
        Set-Content "$pastaHom\estado.json" '{"ultimoNsu":999999999}' -Encoding ASCII
        $r = Chamar $cli POST "$base/api/emitir" $valido
        $e2 = $r.Json
        Ok 'mesmo número é recusado com E0014' ($r.Status -eq 200 -and -not $e2.ok -and ($e2.erros -join ' ') -match 'E0014') $r.Texto
        Ok 'app mostra qual nota o número já gerou' ($e2.jaExistia.chave -eq $e1.chave -and $e2.jaExistia.numero -eq $e1.numero) $r.Texto
        Ok 'nota recuperada volta para o cache' (Test-Path "$pastaHom\nfse\$($e1.chave).xml")

        $repetir = $valido.Clone()
        $repetir.emailTomador = $emailReal
        $r = Chamar $cli POST "$base/api/emitir" $repetir
        $e3 = $r.Json
        Ok 'tentar de novo usa o número seguinte e autoriza' ($e3.ok -and [int64]$e3.nDps -eq [int64]$e1.nDps + 1) $r.Texto
        if ($e3.ok) {
            [xml]$nf3 = (Chamar $cli GET "$base/api/notas/$($e3.chave)/xml?amb=hom").Texto
            if ($emailReal) { Ok 'e-mail real do cliente nunca vai em homologação' ($null -eq (Tomador $nf3 'email')) (Tomador $nf3 'email') }
        }

        Secao 'Leiaute se ajusta sozinho às recusas da Sefin (sem criar nota)'
        # Força a homologação a começar no formato de produção (sem finNFSe, com alíquota): a Sefin recusa
        # (E1235, E0625) e o app tem que ajustar e reenviar até autorizar.
        Set-Content "$pastaHom\leiaute.json" '{"FinNFSe":false,"SemAliquotaMeEpp":false,"TotalTributosSimples":false}' -Encoding ASCII
        $r = Chamar $cli POST "$base/api/emitir" $valido
        $e4 = $r.Json
        Ok 'autoriza depois de se ajustar' ($e4.ok) $r.Texto
        $textoAjustes = ($e4.ajustes -join ' | ')
        Ok 'ajustou o finNFSe e a alíquota' ($textoAjustes -match 'finNFSe' -and $textoAjustes -match 'alíquota') $textoAjustes
        $leiauteSalvo = Get-Content "$pastaHom\leiaute.json" -Raw | ConvertFrom-Json
        Ok 'lembra o formato que passou' ($leiauteSalvo.FinNFSe -and $leiauteSalvo.SemAliquotaMeEpp) ($leiauteSalvo | ConvertTo-Json -Compress)
        if ($e4.ok) { Write-Host "         NFS-e nº $($e4.numero) depois de: $textoAjustes" }
    }

    # ------------------------------------------------------------------ instância e porta
    Secao 'Instância e porta'
    $p2 = Iniciar $exe $Porta "$tmp\saida2.txt"
    $saiu = $p2.WaitForExit(20000)
    Ok 'segunda abertura não duplica (sai e aponta a aberta)' ($saiu -and ((Get-Content "$tmp\saida2.txt" -Raw) -match 'aberto em http')) ((Get-Content "$tmp\saida2.txt" -Raw -ErrorAction SilentlyContinue) + '')

    Encerrar $proc
    $ocupante = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, ($Porta + 1))
    $ocupante.Start()
    try {
        $p3 = Iniciar $exe ($Porta + 1) "$tmp\saida3.txt"
        $url3 = EsperarUrl "$tmp\saida3.txt"
        Ok 'porta ocupada por outro programa: sobe em outra livre' ($url3 -and $url3 -ne "http://127.0.0.1:$($Porta + 1)") "$url3"
        if ($url3) { Ok 'e responde nessa porta' ((Chamar (NovoCliente) GET "$url3/api/info").Json.app -eq 'NfseApp') }
        Encerrar $p3
    }
    finally { $ocupante.Stop() }
}
catch {
    $script:falhou++
    Write-Host "  FALHOU (interrompido) $($_.Exception.Message)"
}
finally {
    foreach ($p in $processos) { Encerrar $p }
    Start-Sleep -Milliseconds 500
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    Remove-Item Env:\NFSEAPP_DADOS -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Resultado: $($script:passou) ok, $($script:falhou) falha(s), $($script:avisos) aviso(s)"
if ($script:falhou -gt 0) { exit 1 } else { exit 0 }
