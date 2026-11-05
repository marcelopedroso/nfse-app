#!/usr/bin/env bash
# Gera as duas edições do app. Não roda testes: a bateria antiga era em PowerShell, que é proibido nesta
# máquina (o antivírus corporativo reage), e ainda não foi reescrita.
#
#   tools/build.sh [normal|comprimido|reduzido]     (padrão: reduzido)
#
#   normal      ~95 MB  runtime .NET inteiro, sem compressão
#   comprimido  ~45 MB  mesmo conteúdo, comprimido dentro do .exe
#   reduzido    ~18 MB  comprimido + remove do .NET o que o app não usa (trimming parcial)
#
# Roda no WSL.
set -euo pipefail

modo=${1:-reduzido}
raiz=$(cd "$(dirname "$0")/.." && pwd)

# Regra do app: nunca chamar o PowerShell. Antivírus corporativo trata isso como comportamento de ataque.
if grep -rniE 'powershell|pwsh' "$raiz/src" --exclude-dir=obj --exclude-dir=bin; then
  echo "o app não pode chamar PowerShell (trechos acima)"; exit 1
fi
comum=(-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
       -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none)

case $modo in
  normal)     extra=() ;;
  comprimido) extra=(-p:EnableCompressionInSingleFile=true) ;;
  reduzido)   extra=(-p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial
                     -p:JsonSerializerIsReflectionEnabledByDefault=true) ;;
  *) echo "modo desconhecido: $modo (use normal, comprimido ou reduzido)"; exit 2 ;;
esac

cd "$raiz/src/NfseApp"
publicar() {  # $1 = teste|producao, demais = opções extras
  local edicao=$1; shift
  # Pasta intermediária própria por modo/edição: nada de reaproveitar compilação de outra variante.
  # Falha de compilação para o build aqui: senão os testes rodariam no .exe da compilação anterior.
  local saida
  if ! saida=$(dotnet publish "${comum[@]}" "${extra[@]}" "$@" \
      -p:IntermediateOutputPath="obj/$modo-$edicao/" -p:OutputPath="bin/$modo-$edicao/" \
      -o "$raiz/dist/$modo/$edicao" 2>&1); then
    echo "$saida" | grep -E "error" | sort -u; echo "não compilou ($edicao)"; exit 1
  fi
  echo "$saida" | grep -E "NfseApp.* -> .*dist" || true
}
publicar teste
publicar producao -p:SoProducao=true

echo
echo "Gerado em dist/$modo/ — sem bateria automática. Teste à mão antes de publicar."
