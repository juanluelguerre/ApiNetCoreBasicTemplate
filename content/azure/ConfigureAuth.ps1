[CmdletBinding()]
param(
    [PSCredential] $Credential,
    [Parameter(Mandatory=$False, HelpMessage='Tenant ID (This is a GUID which represents the "Directory ID" of the AzureAD tenant into which you want to create the apps')]
    [string] $tenantId="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxxx",
    [Parameter(Mandatory=$False, HelpMessage='API Name without suffix and preffix. i.e: Client.Project.API-NAME.Api')]
    [string] $apiName="Items",
    [Parameter(Mandatory=$False, HelpMessage='API port')]
    [string] $apiPort="5001"
)

<#
 This script creates the Azure AD applications needed for this sample and updates the configuration files
 for the visual Studio projects from the data in the Azure AD applications.

 Before running this script you need to install the AzureAD cmdlets as an administrator. 
 For this:
 1) Run Powershell as an administrator
 2) in the PowerShell window, type: Install-Module AzureAD

 There are four ways to run this script. For more information, read the AppCreationScripts.md file in the same folder as this script.
#>

# Adds the requiredAccesses (expressed as a pipe separated string) to the requiredAccess structure
# The exposed permissions are in the $exposedPermissions collection, and the type of permission (Scope | Role) is 
# described in $permissionType
Function AddResourcePermission($requiredAccess, `
                               $exposedPermissions, [string]$requiredAccesses, [string]$permissionType)
{
        foreach($permission in $requiredAccesses.Trim().Split("|"))
        {
            foreach($exposedPermission in $exposedPermissions)
            {
                if ($exposedPermission.Value -eq $permission)
                 {
                    $resourceAccess = New-Object Microsoft.Open.AzureAD.Model.ResourceAccess
                    $resourceAccess.Type = $permissionType # Scope = Delegated permissions | Role = Application permissions
                    $resourceAccess.Id = $exposedPermission.Id # Read directory data
                    $requiredAccess.ResourceAccess.Add($resourceAccess)
                 }
            }
        }
}

#
# Exemple: GetRequiredPermissions "Microsoft Graph"  "Graph.Read|User.Read"
# See also: http://stackoverflow.com/questions/42164581/how-to-configure-a-new-azure-ad-application-through-powershell
Function GetRequiredPermissions([string] $applicationDisplayName, [string] $requiredDelegatedPermissions, [string]$requiredApplicationPermissions, $servicePrincipal)
{
    # If we are passed the service principal we use it directly, otherwise we find it from the display name (which might not be unique)
    if ($servicePrincipal)
    {
        $sp = $servicePrincipal
    }
    else
    {
        $sp = Get-AzureADServicePrincipal -Filter "DisplayName eq '$applicationDisplayName'"
    }
    $appid = $sp.AppId
    $requiredAccess = New-Object Microsoft.Open.AzureAD.Model.RequiredResourceAccess
    $requiredAccess.ResourceAppId = $appid 
    $requiredAccess.ResourceAccess = New-Object System.Collections.Generic.List[Microsoft.Open.AzureAD.Model.ResourceAccess]

    # $sp.Oauth2Permissions | Select Id,AdminConsentDisplayName,Value: To see the list of all the Delegated permissions for the application:
    if ($requiredDelegatedPermissions)
    {
        AddResourcePermission $requiredAccess -exposedPermissions $sp.Oauth2Permissions -requiredAccesses $requiredDelegatedPermissions -permissionType "Scope"
    }
    
    # $sp.AppRoles | Select Id,AdminConsentDisplayName,Value: To see the list of all the Application permissions for the application
    if ($requiredApplicationPermissions)
    {
        AddResourcePermission $requiredAccess -exposedPermissions $sp.AppRoles -requiredAccesses $requiredApplicationPermissions -permissionType "Role"
    }
    return $requiredAccess
}


# Replace the value of an appsettings of a given key in an XML App.Config file.
Function ReplaceSetting([string] $configFilePath, [string] $key, [string] $newValue)
{
    [xml] $content = Get-Content $configFilePath
    $appSettings = $content.configuration.appSettings; 
    $keyValuePair = $appSettings.SelectSingleNode("descendant::add[@key='$key']")
    if ($keyValuePair)
    {
        $keyValuePair.value = $newValue;
    }
    else
    {
        Throw "Key '$key' not found in file '$configFilePath'"
    }
   $content.save($configFilePath)
}


Function UpdateLine([string] $line, [string] $value)
{
    $index = $line.IndexOf('=')
    $delimiter = ';'
    if ($index -eq -1)
    {
        $index = $line.IndexOf(':')
        $delimiter = ','
    }
    if ($index -ige 0)
    {
        $line = $line.Substring(0, $index+1) + " "+'"'+$value+'"'+$delimiter
    }
    return $line
}

Function UpdateTextFile([string] $configFilePath, [System.Collections.HashTable] $dictionary)
{
    $lines = Get-Content $configFilePath
    $index = 0
    while($index -lt $lines.Length)
    {
        $line = $lines[$index]
        foreach($key in $dictionary.Keys)
        {
            if ($line.Contains($key))
            {
                $lines[$index] = UpdateLine $line $dictionary[$key]
            }
        }
        $index++
    }

    Set-Content -Path $configFilePath -Value $lines -Force
}

Set-Content -Value "<html><body><table>" -Path createdApps.html
Add-Content -Value "<thead><tr><th>Application</th><th>AppId</th><th>Url in the Azure portal</th></tr></thead><tbody>" -Path createdApps.html

Function ConfigureApplications
{
<#.Description
   This function creates the Azure AD applications for the sample in the provided Azure AD tenant and updates the
   configuration files in the client and service project  of the visual studio solution (App.Config and Web.Config)
   so that they are consistent with the Applications parameters
#> 

    # $tenantId is the Active Directory Tenant. This is a GUID which represents the "Directory ID" of the AzureAD tenant
    # into which you want to create the apps. Look it up in the Azure portal in the "Properties" of the Azure AD.

    # Login to Azure PowerShell (interactive if credentials are not already provided:
    # you'll need to sign-in with creds enabling your to create apps in the tenant)
    if (!$Credential -and $TenantId)
    {
        $creds = Connect-AzureAD -TenantId $tenantId
        # $creds = Connect-AzAccount -TenantId $tenantId
    }
    else
    {
        if (!$TenantId)
        {
            $creds = Connect-AzureAD -Credential $Credential
            # $creds = Connect-AzAccount -Credential $Credential            
        }
        else
        {
            $creds = Connect-AzureAD -TenantId $tenantId -Credential $Credential
            # $creds = Connect-AzAccount -TenantId $tenantId -Credential $Credential
        }
    }

    if (!$tenantId)
    {
        $tenantId = $creds.Tenant.Id
    }

    $tenant = Get-AzureADTenantDetail
    $tenantName =  ($tenant.VerifiedDomains | Where-Object { $_._Default -eq $True }).Name

    # Get the user running the script
    # $user = Get-AzureADUser -ObjectId $creds.Account.Id
    $user = Get-AzureADUser -Filter "Mail eq '$creds.Account'"

    # Take care: Key Sensitive for audiende parameters
    $fullApiName = "ElGuerre.$apiName"
    # $fullWebName = "ElGuerre.Web"

   # Create the service AAD application
   Write-Host "Creating the AAD application (API/Service)"
   $serviceAadApplication = New-AzureADApplication -DisplayName "$fullApiName.Api" `
                                                   -IdentifierUris "https://$tenantName/$fullApiName.Api" `
                                                   -PublicClient $False                                                   
                                                   # -Oauth2AllowImplicitFlow $True
                                                   


   $currentAppId = $serviceAadApplication.AppId
   $serviceServicePrincipal = New-AzureADServicePrincipal -AppId $currentAppId -Tags {WindowsAzureActiveDirectoryIntegratedApp}

   # add the user running the script as an app owner
   Write-Host "Adding new application '$($serviceServicePrincipal.DisplayName)'..."
   
   # Add-AzureADApplicationOwner -ObjectId $serviceAadApplication.ObjectId -RefObjectId $user.ObjectId
   #Add-AzureADApplicationOwner -ObjectId $serviceAadApplication.ObjectId -RefObjectId $user.Account

   Write-Host "'$($user.UserPrincipalName)' added as an application owner to app '$($serviceServicePrincipal.DisplayName)'"

   Write-Host "Done creating the service application ($fullApiName)"

   # URL of the AAD application in the Azure portal
   $servicePortalUrl = "https://portal.azure.com/#@"+$tenantName+"/blade/Microsoft_AAD_IAM/ApplicationBlade/appId/"+$serviceAadApplication.AppId+"/objectId/"+$serviceAadApplication.ObjectId
   Add-Content -Value "<tr><td>API</td><td>$currentAppId</td><td><a href='$servicePortalUrl'>$fullApiName</a></td></tr>" -Path createdApps.html

   # Update config file for 'service'   
   $configFile = $pwd.Path + "\..\src\$fullApiName.Api\appsettings.json"   
   Write-Host "Updating '$configFile'..."
   $dictionary = @{ "Domain" = $tenantName;"TenantId" = $tenantId;"ClientId" = $serviceAadApplication.AppId };
   UpdateTextFile -configFilePath $configFile -dictionary $dictionary
   Write-Host "'$configFile' updated !"


   ######################################################################
   # WEB  CONFIGURATION
   ######################################################################
    #    # Create the client AAD application
    #    Write-Host "Creating the AAD application ($fullWebName)"
    #    $webAadApplication = New-AzureADApplication -DisplayName "$fullWebName" `
    #                                                   -ReplyUrls "https://$fullWebName" `
    #                                                   -PublicClient $True


    # $currentAppId = $webAadApplication.AppId
    # $clientServicePrincipal = New-AzureADServicePrincipal -AppId $currentAppId -Tags {WindowsAzureActiveDirectoryIntegratedApp}
 
    # # add the user running the script as an app owner
    # Add-AzureADApplicationOwner -ObjectId $webAadApplication.ObjectId -RefObjectId $user.ObjectId
    # Write-Host "'$($user.UserPrincipalName)' added as an application owner to app '$($clientServicePrincipal.DisplayName)'"


    #    Write-Host "Done creating the Web application ($fullWebName)"

    #    # URL of the AAD application in the Azure portal
    #    $clientPortalUrl = "https://portal.azure.com/#@"+$tenantName+"/blade/Microsoft_AAD_IAM/ApplicationBlade/appId/"+$webAadApplication.AppId+"/objectId/"+$webAadApplication.ObjectId
    #    Add-Content -Value "<tr><td>Web</td><td>$currentAppId</td><td><a href='$clientPortalUrl'>$fullWebName</a></td></tr>" -Path createdApps.html

    #    $requiredResourcesAccess = New-Object System.Collections.Generic.List[Microsoft.Open.AzureAD.Model.RequiredResourceAccess]

    #    # Add Required Resources Access (from 'client' to 'service')
    #    Write-Host "Getting access from 'client' to 'service'"
    #    $requiredPermissions = GetRequiredPermissions -applicationDisplayName "$fullApiName" `
    #                                                  -requiredDelegatedPermissions "user_impersonation";
    #    $requiredResourcesAccess.Add($requiredPermissions)


    #    Set-AzureADApplication -ObjectId $webAadApplication.ObjectId -RequiredResourceAccess $requiredResourcesAccess
    #    Write-Host "Granted permissions."



   # Update config file for 'Web'
   #    $configFile = $pwd.Path + "\..\TodoListClient\appSetting.json"
   #    Write-Host "Updating the sample code ($configFile)"
   #    ReplaceSetting -configFilePath $configFile -key "ida:Tenant" -newValue $tenantName
   #    ReplaceSetting -configFilePath $configFile -key "ida:ClientId" -newValue $webAadApplication.AppId
   #    ReplaceSetting -configFilePath $configFile -key "ida:RedirectUri" -newValue $webAadApplication.ReplyUrls
   #    ReplaceSetting -configFilePath $configFile -key "todo:TodoListResourceId" -newValue $serviceAadApplication.AppId
   #    ReplaceSetting -configFilePath $configFile -key "todo:TodoListBaseAddress" -newValue $serviceAadApplication.HomePage
   
   # Add-Content -Value "</tbody></table></body></html>" -Path createdApps.html  

    ######################################################################
}

# Run interactively (will ask you for the tenant ID)
ConfigureApplications -Credential $Credential -tenantId $TenantId -apiName $ApplicationName - apiPort $apiPort