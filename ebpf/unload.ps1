# ELEVATED. Detaches and unloads the program by id.
param([Parameter(Mandatory = $true)][int]$Id)
$ErrorActionPreference = 'Stop'
netsh ebpf delete program $Id
netsh ebpf show programs
