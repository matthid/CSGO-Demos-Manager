﻿// Template for webpack.config.js in Fable projects
// Find latest version in https://github.com/fable-compiler/webpack-config-template

// In most cases, you'll only need to edit the CONFIG object (after dependencies)
// See below if you need better fine-tuning of Webpack options

// Dependencies. Also required: core-js, fable-loader, fable-compiler, @babel/core,
// @babel/preset-env, babel-loader, sass, sass-loader, css-loader, style-loader, file-loader
var path = require('path');
var webpack = require('webpack');
var HtmlWebpackPlugin = require('html-webpack-plugin');
var CopyWebpackPlugin = require('copy-webpack-plugin');
var MiniCssExtractPlugin = require('mini-css-extract-plugin');

var CONFIG = {
    // The tags to include the generated JS and CSS will be automatically injected in the HTML template
    // See https://github.com/jantimon/html-webpack-plugin
    indexHtmlTemplate: './src/Client/index.html',
    fsharpEntry: './src/Client/Client.fsproj',
    cssEntry: './src/Client/style.scss',
    assetsDir: './src/Client/public',
    devServerPort: 8080,
    // When using webpack-dev-server, you may need to redirect some calls
    // to a external API server. See https://webpack.js.org/configuration/dev-server/#devserver-proxy
    devServerProxy: {
        // redirect requests that start with /api/* to the server on port 8085
        '/api/*': {
            target: 'http://localhost:' + (process.env.SERVER_PROXY_PORT || "8085"),
               changeOrigin: true
           },
        // redirect websocket requests that start with /socket/* to the server on the port 8085
        '/socket/*': {
            target: 'http://localhost:' + (process.env.SERVER_PROXY_PORT || "8085"),
            ws: true
           }
       },
    // Use babel-preset-env to generate JS compatible with most-used browsers.
    // More info at https://babeljs.io/docs/en/next/babel-preset-env.html
    babel: {
        presets: [
            ['@babel/preset-env', {
                modules: false,
                // This adds polyfills when needed. Requires core-js dependency.
                // See https://babeljs.io/docs/en/babel-preset-env#usebuiltins
                useBuiltIns: 'usage',
                corejs: 3
            }]
        ],
    }
}

var isDevServer = process.argv.find(v => v.indexOf('webpack-dev-server') !== -1);
// If we're running the webpack-dev-server, assume we're in development mode
var isProduction = !isDevServer;
console.log('Bundling for ' + (isProduction ? 'production' : 'development') + '...');

// The HtmlWebpackPlugin allows us to use a template for the index.html page
// and automatically injects <script> or <link> tags for generated bundles.
var commonPlugins = [
    new HtmlWebpackPlugin({
        filename: 'index.html',
        template: resolve(CONFIG.indexHtmlTemplate)
    })
];

// - fable-loader: transforms F# into JS
// - babel-loader: transforms JS to old syntax (compatible with old browsers)
// - sass-loaders: transforms SASS/SCSS into JS
// - file-loader: Moves files referenced in the code (fonts, images) into output folder
const module_rules = {
    rules: [
        {
            test: /\.fs(x|proj)?$/,
            use: {
                loader: 'fable-loader',
                options: {
                    babel: CONFIG.babel
                }
            }
        },
        {
            test: /\.js$/,
            exclude: /node_modules/,
            use: {
                loader: 'babel-loader',
                options: CONFIG.babel
            },
        },
        {
            test: /\.(sass|scss|css)$/,
            use: [
                isProduction
                    ? MiniCssExtractPlugin.loader
                    : 'style-loader',
                'css-loader',
                {
                  loader: 'sass-loader',
                  options: { implementation: require('sass') }
                }
            ],
        },
        {
            test: /\.(png|jpg|jpeg|gif|svg|woff|woff2|ttf|eot)(\?.*)?$/,
            use: ['file-loader']
        }
    ]
};

const electron_config = {
    // In development, split the JavaScript and CSS files in order to
    // have a faster HMR support. In production bundle styles together
    // with the code because the MiniCssExtractPlugin will extract the
    // CSS in a separate files.
    entry: isProduction ? {
        app: [resolve('./src/Client/target.electron.js'), resolve(CONFIG.fsharpEntry), resolve(CONFIG.cssEntry)]
    } : {
            app: [resolve('./src/Client/target.electron.js'), resolve(CONFIG.fsharpEntry)],
            style: [resolve(CONFIG.cssEntry)]
        },
    target: 'electron-renderer',
    // Add a hash to the output file name in production
    // to prevent browser caching if code changes
    output: {
        path: resolve('./publish/Client'),
        filename: isProduction ? '[name].[hash].js' : '[name].js'
    },
    mode: isProduction ? 'production' : 'development',
    devtool: isProduction ? 'source-map' : 'eval-source-map',
    optimization: {
        splitChunks: {
            chunks: 'all'
        },
    },
    // Besides the HtmlPlugin, we use the following plugins:
    // PRODUCTION
    //      - MiniCssExtractPlugin: Extracts CSS from bundle to a different file
    //          To minify CSS, see https://github.com/webpack-contrib/mini-css-extract-plugin#minimizing-for-production    
    //      - CopyWebpackPlugin: Copies static assets to output directory
    // DEVELOPMENT
    //      - HotModuleReplacementPlugin: Enables hot reloading when code changes without refreshing
    plugins: isProduction ?
        commonPlugins.concat([
            new MiniCssExtractPlugin({ filename: 'style.[hash].css' }),
            new CopyWebpackPlugin([{ from: resolve(CONFIG.assetsDir) }]),
        ])
        : commonPlugins.concat([
            new webpack.HotModuleReplacementPlugin(),
        ]),
    resolve: {
        // See https://github.com/fable-compiler/Fable/issues/1490
        symlinks: false
    },
    module: module_rules,
    
    node: {
        process: false
    }
};


const client_config = {
    // In development, split the JavaScript and CSS files in order to
    // have a faster HMR support. In production bundle styles together
    // with the code because the MiniCssExtractPlugin will extract the
    // CSS in a separate files.
    entry: isProduction ? {
        app: [resolve('./src/Client/target.web.js'), resolve(CONFIG.fsharpEntry), resolve(CONFIG.cssEntry)]
    } : {
            app: [resolve('./src/Client/target.web.js'), resolve(CONFIG.fsharpEntry)],
            style: [resolve(CONFIG.cssEntry)]
        },
    // Add a hash to the output file name in production
    // to prevent browser caching if code changes
    output: {
        path: resolve('./src/Client/deploy'),
        filename: isProduction ? '[name].[hash].js' : '[name].js'
    },
    mode: isProduction ? 'production' : 'development',
    devtool: isProduction ? 'source-map' : 'eval-source-map',
    optimization: {
        splitChunks: {
            chunks: 'all'
        },
    },
    // Besides the HtmlPlugin, we use the following plugins:
    // PRODUCTION
    //      - MiniCssExtractPlugin: Extracts CSS from bundle to a different file
    //          To minify CSS, see https://github.com/webpack-contrib/mini-css-extract-plugin#minimizing-for-production    
    //      - CopyWebpackPlugin: Copies static assets to output directory
    // DEVELOPMENT
    //      - HotModuleReplacementPlugin: Enables hot reloading when code changes without refreshing
    plugins: isProduction ?
        commonPlugins.concat([
            new MiniCssExtractPlugin({ filename: 'style.[hash].css' }),
            new CopyWebpackPlugin([{ from: resolve(CONFIG.assetsDir) }]),
        ])
        : commonPlugins.concat([
            new webpack.HotModuleReplacementPlugin(),
        ]),
    resolve: {
        // See https://github.com/fable-compiler/Fable/issues/1490
        symlinks: false
    },
    // Configuration for webpack-dev-server
    devServer: {
        publicPath: '/',
        //contentBase: resolve('./src/Client/deploy'),
        //contentBase: resolve(CONFIG.assetsDir),
        host: '0.0.0.0',
        port: CONFIG.devServerPort,
        proxy: CONFIG.devServerProxy,
        hot: true,
        inline: true
    },
    module: module_rules,
    
    node: {
        process: false
    }
};

// First one is used by webpack-dev-server
module.exports = isDevServer ? client_config : [ client_config, electron_config ];

function resolve(filePath) {
    return path.isAbsolute(filePath) ? filePath : path.join(__dirname, filePath);
}
