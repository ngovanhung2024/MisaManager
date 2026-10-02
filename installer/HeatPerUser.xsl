<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:wix="http://wixtoolset.org/schemas/v4/wxs">
  <xsl:output method="xml" indent="yes"/>

  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()"/>
    </xsl:copy>
  </xsl:template>

  <xsl:template match="wix:File/@KeyPath">
    <xsl:attribute name="KeyPath">no</xsl:attribute>
  </xsl:template>

  <xsl:template match="wix:Component[wix:File]">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()"/>
      <wix:RegistryValue Root="HKCU"
                         Key="Software\MisaManager\Components\{@Id}"
                         Name="Installed"
                         Type="integer"
                         Value="1"
                         KeyPath="yes"/>
      <xsl:if test="ancestor::wix:Directory and not(preceding-sibling::wix:Component[wix:File])">
        <wix:RemoveFolder Id="remove_{ancestor::wix:Directory[1]/@Id}" On="uninstall"/>
      </xsl:if>
    </xsl:copy>
  </xsl:template>
</xsl:stylesheet>
