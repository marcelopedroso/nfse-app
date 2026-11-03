"""Gera src/NfseApp/assets/municipios.csv (código IBGE;nome;UF) a partir da API oficial do IBGE.
O DANFSe (NT 008/2026) pede o nome do município, mas o XML da NFS-e só traz o código.
Rode: python3 tools/gerar_municipios.py"""
import csv, gzip, io, json, os, urllib.request

URL = "https://servicodados.ibge.gov.br/api/v1/localidades/municipios?view=nivelado"
destino = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "NfseApp", "assets", "municipios.csv")

with urllib.request.urlopen(URL, timeout=60) as r:
    bruto = r.read()
if bruto[:2] == b"\x1f\x8b":  # o IBGE responde em gzip mesmo sem pedir
    bruto = gzip.decompress(bruto)
dados = json.loads(bruto)
linhas = sorted({(str(m["municipio-id"]), m["municipio-nome"], m["UF-sigla"]) for m in dados})
saida = io.StringIO()
csv.writer(saida, delimiter=";", lineterminator="\n").writerows(linhas)
open(destino, "w", encoding="utf-8").write(saida.getvalue())
print(f"{len(linhas)} municípios -> {os.path.normpath(destino)}")
